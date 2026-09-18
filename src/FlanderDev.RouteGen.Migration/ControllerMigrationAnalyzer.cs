using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace FlanderDev.RouteGen.Migration;

/// <summary>
/// Reports <see cref="MigrationDiagnostics.ControllerCanBeMigrated"/> (RGM0001, Info) on any
/// attribute-routed controller with at least one migratable action, unless a type named
/// "I{Stem}Api" already exists anywhere in the solution, see
/// <see cref="ControllerMigrationReader"/> for the shared detection/translation logic this and
/// the code fix (in the separate FlanderDev.RouteGen.Migration.CodeFixes assembly, see this
/// project's csproj for why) both build on.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ControllerMigrationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [MigrationDiagnostics.ControllerCanBeMigrated];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
        if (!ControllerMigrationReader.DerivesFromControllerBase(type)) return;
        if (!ControllerMigrationReader.HasAttributeRouting(type)) return;

        // Full translation (not just "does anything qualify") so the analyzer and the fix agree
        // on exactly the same set of migratable actions, a controller whose only [Http*] action
        // turns out to be unsupported (e.g. returns a FileResult) should not show the diagnostic
        // at all, since there'd be nothing for the fix to actually generate.
        var model = ControllerMigrationReader.TryBuildModel(type);
        if (model is null || model.Actions.Count == 0) return;

        if (ControllerMigrationReader.TypeExistsInCompilation(context.Compilation, model.InterfaceName))
            return;

        var location = type.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return;

        context.ReportDiagnostic(Diagnostic.Create(
            MigrationDiagnostics.ControllerCanBeMigrated, location, type.Name));
    }
}
