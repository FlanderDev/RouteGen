using FlanderDev.RouteGen.Abstractions;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FlanderDev.RouteGen.Generators;

/// <summary>
/// Finds every <c>[ApiRoute]</c>-decorated interface reachable from the current compilation --
/// declared either in this project's own source, or in a referenced project/assembly (the normal
/// case: the interface lives in a Shared project, and this generator runs in Server/Client,
/// which only reference Shared) -- and emits:
///   - an abstract MVC controller base class, when the compilation references
///     <c>Microsoft.AspNetCore.Mvc.ControllerBase</c> (i.e. this is the ASP.NET Core server
///     project), or
///   - a concrete <c>HttpClient</c>-backed implementation of the interface otherwise (i.e. this
///     is the Blazor WebAssembly client project, or any other C# client project).
///
/// Both emission paths, plus the collision/type diagnostics, run off the exact same parsed
/// <see cref="ApiInterfaceModel"/> so the server and client structurally cannot drift.
///
/// Implementation note: this deliberately does NOT use
/// <c>context.SyntaxProvider.ForAttributeWithMetadataName</c>. That API only enumerates syntax
/// trees belonging to the compilation currently being built, so it would never see an interface
/// declared in a referenced Shared project -- which is exactly the topology this package targets.
/// Instead this walks symbols (current compilation's assembly + every referenced assembly) via
/// <see cref="CompilationProvider"/>, which sees referenced-project symbols regardless of where
/// their syntax lives.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ApiContractGenerator : IIncrementalGenerator
{
    /// <inheritdoc cref="IIncrementalGenerator.Initialize"/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.CompilationProvider.Select(static (compilation, ct) =>
        {
            bool isServer = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase") is not null;

            // Bail out entirely if this compilation doesn't reference the RouteGen abstractions --
            // there is nothing to find, and no reason to pay for walking every referenced assembly.
            if (FindType(compilation, nameof(ApiRouteAttribute)) is null)
                return (Interfaces: [], IsServer: isServer);

            var found = new List<INamedTypeSymbol>();
            var seen = new HashSet<string>();

            CollectAttributedInterfaces(compilation.Assembly.GlobalNamespace, found, seen, ct);
            foreach (var referencedAssembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // Skip framework/BCL assemblies -- they cannot contain [ApiRoute] interfaces, and
                // walking their (very large) namespace trees would be pure wasted work.
                string name = referencedAssembly.Name;
                if (name.StartsWith("System") || name.StartsWith("Microsoft.NET") ||
                    name.StartsWith("netstandard") || name.StartsWith("mscorlib") ||
                    name.StartsWith("Microsoft.CSharp") || name.StartsWith("WindowsBase"))
                {
                    continue;
                }
                CollectAttributedInterfaces(referencedAssembly.GlobalNamespace, found, seen, ct);
            }

            return (Interfaces: found, IsServer: isServer);
        });

        context.RegisterSourceOutput(provider, static (spc, data) =>
        {
            foreach (var interfaceSymbol in data.Interfaces)
            {
                var diagnostics = new List<Diagnostic>();
                var model = ApiInterfaceReader.TryParse(interfaceSymbol, diagnostics);
                foreach (var d in diagnostics) spc.ReportDiagnostic(d);
                if (model is null || model.Methods.Count == 0) continue;

                string source = data.IsServer
                    ? ServerControllerEmitter.Emit(model)
                    : ClientImplementationEmitter.Emit(model);

                string fileNameHint = (data.IsServer ? "Server_" : "Client_") + model.InterfaceName + ".g.cs";
                spc.AddSource(fileNameHint, source);
            }
        });
    }

    /// <summary>Recursively collects every <c>[ApiRoute]</c>-decorated interface (including nested types) under <paramref name="ns"/> into <paramref name="results"/>, deduplicated via <paramref name="seen"/>.</summary>
    private static void CollectAttributedInterfaces(
        INamespaceSymbol ns, List<INamedTypeSymbol> results, HashSet<string> seen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                CollectAttributedInterfaces(childNamespace, results, seen, ct);
            }
            else if (member is INamedTypeSymbol type)
            {
                CollectIfAttributed(type, results, seen);
                // Interfaces can nest other types (rare, but cheap to check) -- walk them too.
                foreach (var nested in type.GetTypeMembers())
                    CollectIfAttributed(nested, results, seen);
            }
        }
    }

    /// <summary>Adds <paramref name="type"/> to <paramref name="results"/> if it's an interface carrying <c>[ApiRoute]</c> and hasn't already been seen.</summary>
    private static void CollectIfAttributed(INamedTypeSymbol type, List<INamedTypeSymbol> results, HashSet<string> seen)
    {
        if (type.TypeKind != TypeKind.Interface) return;
        if (!type.GetAttributes().Any(a => IsAttribute(a.AttributeClass, nameof(ApiRouteAttribute)))) return;

        string key = type.ToDisplayString();
        if (seen.Add(key)) results.Add(type);
    }

    /// <summary>Finds a type named <paramref name="simpleName"/> in <paramref name="compilation"/>'s own assembly or any referenced assembly.</summary>
    private static INamedTypeSymbol? FindType(Compilation compilation, string simpleName)
    {
        var found = FindType(compilation.Assembly.GlobalNamespace, simpleName);
        if (found is not null)
            return found;

        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            found = FindType(assembly.GlobalNamespace, simpleName);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Recursively searches <paramref name="ns"/> (including nested types) for a type named <paramref name="simpleName"/>.</summary>
    private static INamedTypeSymbol? FindType(INamespaceSymbol ns, string simpleName)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                var found = FindType(childNamespace, simpleName);
                if (found is not null)
                    return found;
            }
            else if (member is INamedTypeSymbol type && type.Name == simpleName)
            {
                return type;
            }

            if (member is INamedTypeSymbol container)
            {
                foreach (var nested in container.GetTypeMembers())
                {
                    if (nested.Name == simpleName)
                        return nested;
                }
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="attributeType"/> is (or is named like) <paramref name="simpleName"/>, tolerating attributes from any namespace.</summary>
    private static bool IsAttribute(INamedTypeSymbol? attributeType, string simpleName)
        => attributeType is not null &&
           (attributeType.Name == simpleName ||
            attributeType.ToDisplayString().EndsWith("." + simpleName, System.StringComparison.Ordinal));
}
