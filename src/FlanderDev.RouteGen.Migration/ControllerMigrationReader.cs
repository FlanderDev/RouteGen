using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace FlanderDev.RouteGen.Migration;

/// <summary>
/// Reads an attribute-routed ASP.NET Core controller and translates it into a
/// <see cref="ControllerMigrationModel"/> -- the reverse of what <c>ApiInterfaceReader</c> (in
/// RouteGen.Generators) does. Shared between <see cref="ControllerMigrationAnalyzer"/> (which
/// only needs "does at least one action qualify") and <see cref="ControllerMigrationCodeFixProvider"/>
/// (which needs the full translated model), so the two can never silently disagree about what
/// counts as migratable.
/// </summary>
internal static class ControllerMigrationReader
{
    private static readonly SymbolDisplayFormat FullyQualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Strips a trailing "Controller" suffix, e.g. "ModsController" -> "Mods".</summary>
    public static string StemFromControllerName(string controllerName) =>
        controllerName.EndsWith("Controller") && controllerName.Length > "Controller".Length
            ? controllerName.Substring(0, controllerName.Length - "Controller".Length)
            : controllerName;

    /// <summary>True when <paramref name="type"/> derives (transitively) from a type named <c>ControllerBase</c> or <c>Controller</c>.</summary>
    public static bool DerivesFromControllerBase(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            if (b.Name is "ControllerBase" or "Controller")
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> has at least one <c>[Http*]</c>-attributed method --
    /// the sufficient signal for "this uses attribute routing," since convention-routed
    /// controllers never carry these attributes at all (they rely purely on method-name
    /// conventions matched against a registered route template elsewhere).
    /// </summary>
    public static bool HasAttributeRouting(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IMethodSymbol>().Any(m => GetHttpVerbAttributes(m).Any());

    /// <summary>
    /// True when a type named <paramref name="simpleName"/> already exists anywhere visible to
    /// <paramref name="compilation"/> (its own assembly or any referenced one) -- used to suppress
    /// RGM0001 once a controller's interface has already been generated, even before the user has
    /// done the manual step of rewriting the controller itself to stop matching this diagnostic.
    /// </summary>
    public static bool TypeExistsInCompilation(Compilation compilation, string simpleName) =>
        compilation.GetSymbolsWithName(n => n == simpleName, SymbolFilter.Type).Any();

    /// <summary>
    /// Builds the full <see cref="ControllerMigrationModel"/> for <paramref name="controllerType"/>.
    /// Returns null only if, after mapping, there are zero surviving actions (nothing to emit) --
    /// individual unsupported actions/parameters are skipped and noted, never a reason to abort
    /// the whole controller (Q4(a)).
    /// </summary>
    public static ControllerMigrationModel? TryBuildModel(INamedTypeSymbol controllerType)
    {
        string baseRoute = ResolveBaseRoute(controllerType);

        var model = new ControllerMigrationModel(
            controllerType.ContainingNamespace.IsGlobalNamespace
                ? ""
                : controllerType.ContainingNamespace.ToDisplayString(),
            controllerType.Name,
            baseRoute);

        var (classAuthorize, classRoles, classPolicy) = ReadAuthorize(controllerType.GetAttributes());

        foreach (var method in controllerType.GetMembers().OfType<IMethodSymbol>())
        {
            var httpAttrs = GetHttpVerbAttributes(method).ToList();
            if (httpAttrs.Count == 0) continue; // not an action -- not a skip, just not applicable.

            if (TryBuildAction(method, httpAttrs, baseRoute, out var action, out string? skipReason))
            {
                if (!action!.HasAuthorize && !action.AllowAnonymous && classAuthorize)
                {
                    action.HasAuthorize = true;
                    action.Roles = classRoles;
                    action.Policy = classPolicy;
                }

                CollectLocalTypes(action, controllerType.ContainingAssembly, model.LocalTypeNames);
                model.Actions.Add(action);
            }
            else
            {
                model.SkippedActionNotes.Add(skipReason!);
            }
        }

        return model.Actions.Count == 0 && model.SkippedActionNotes.Count == 0 ? null : model;
    }

    /// <summary>Resolves a controller's base route, substituting a "[controller]" token (if present) with the controller's name stem.</summary>
    private static string ResolveBaseRoute(INamedTypeSymbol controllerType)
    {
        var routeAttr = controllerType.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "RouteAttribute");

        string template = routeAttr is { ConstructorArguments.Length: > 0 }
            ? routeAttr.ConstructorArguments[0].Value as string ?? "[controller]"
            : "[controller]";

        return template.Replace("[controller]", StemFromControllerName(controllerType.Name));
    }

    /// <summary>Every <c>[Http*]</c> attribute on <paramref name="method"/>, in declaration order.</summary>
    private static IEnumerable<AttributeData> GetHttpVerbAttributes(IMethodSymbol method) =>
        method.GetAttributes().Where(a => a.AttributeClass?.Name is
            "HttpGetAttribute" or "HttpPostAttribute" or "HttpPutAttribute" or
            "HttpDeleteAttribute" or "HttpPatchAttribute");

    /// <summary>Translates one action method. Returns false (with a human-readable reason) for anything RouteGen has no vocabulary for -- see the class-level remarks on the skip/carry-over split.</summary>
    private static bool TryBuildAction(
        IMethodSymbol method,
        List<AttributeData> httpAttrs,
        string baseRoute,
        out ActionMigrationModel? model,
        out string? skipReason)
    {
        model = null;

        if (httpAttrs.Count > 1)
        {
            skipReason = $"action '{method.Name}' has more than one HTTP verb attribute; RouteGen supports exactly one verb per method";
            return false;
        }

        string verb = httpAttrs[0].AttributeClass!.Name switch
        {
            "HttpGetAttribute" => "Get",
            "HttpPostAttribute" => "Post",
            "HttpPutAttribute" => "Put",
            "HttpDeleteAttribute" => "Delete",
            "HttpPatchAttribute" => "Patch",
            _ => "Get"
        };

        string? routeSuffix = httpAttrs[0].ConstructorArguments.Length > 0
            ? httpAttrs[0].ConstructorArguments[0].Value as string
            : null;

        // "[action]" always resolves unambiguously to the current action's own name -- unlike
        // "[controller]", there's no scenario where this token is genuinely ambiguous.
        if (routeSuffix is not null)
            routeSuffix = routeSuffix.Replace("[action]", method.Name);

        if (!TryResolveResponseType(method, out string? responseType, out bool isStream, out skipReason))
            return false;

        model = new ActionMigrationModel(method.Name, verb, routeSuffix)
        {
            ResponseTypeFullName = responseType,
            IsStreamResponse = isStream,
        };

        var (methodAuthorize, roles, policy) = ReadAuthorize(method.GetAttributes());
        model.HasAuthorize = methodAuthorize;
        model.Roles = roles;
        model.Policy = policy;
        model.AllowAnonymous = method.GetAttributes().Any(a => a.AttributeClass?.Name == "AllowAnonymousAttribute");

        foreach (var attr in method.GetAttributes())
        {
            string? name = attr.AttributeClass?.Name;

            if (name is "ProducesAttribute" or "ConsumesAttribute" or "ApiVersionAttribute" or
                "ServiceFilterAttribute" or "TypeFilterAttribute" or "ProducesResponseTypeAttribute")
            {
                model.DroppedAttributeNotes.Add(
                    "[" + name!.Substring(0, name.Length - "Attribute".Length) +
                    "] was dropped -- RouteGen doesn't model this; add it back on the concrete controller after migration if still needed");
            }
        }

        string combinedRoute = CombineRoutes(baseRoute, routeSuffix);
        var routeTokenNames = ExtractRouteTokenNames(combinedRoute);

        foreach (var param in method.Parameters)
        {
            if (!TryMapParameter(param, routeTokenNames, out var mappedParam, out string? paramSkipReason))
            {
                skipReason = $"action '{method.Name}' {paramSkipReason}";
                model = null;
                return false;
            }

            model.Parameters.Add(mappedParam!);
        }

        skipReason = null;
        return true;
    }

    /// <summary>
    /// Unwraps <c>Task&lt;T&gt;</c>/<c>ActionResult&lt;T&gt;</c> down to the response type
    /// RouteGen should generate, or fails (with a reason) for shapes with no recoverable JSON
    /// response type: bare <c>IActionResult</c>, bare <c>ActionResult</c>, or a concrete
    /// non-JSON result type (file/content/redirect results).
    /// </summary>
    private static bool TryResolveResponseType(
        IMethodSymbol method, out string? responseTypeFullName, out bool isStream, out string? skipReason)
    {
        responseTypeFullName = null;
        isStream = false;
        skipReason = null;

        ITypeSymbol? unwrapped = method.ReturnType;

        if (unwrapped is INamedTypeSymbol { Name: "Task", IsGenericType: true } taskT)
            unwrapped = taskT.TypeArguments[0];
        else if (unwrapped is INamedTypeSymbol { Name: "Task", IsGenericType: false })
            unwrapped = null; // bare Task -- no response body, nothing further to resolve.

        if (unwrapped is INamedTypeSymbol { Name: "ActionResult", IsGenericType: true } actionResultT)
        {
            unwrapped = actionResultT.TypeArguments[0];
        }
        else if (unwrapped is INamedTypeSymbol { Name: "ActionResult", IsGenericType: false })
        {
            skipReason = $"action '{method.Name}' returns bare ActionResult with no type argument, so RouteGen has no response type to generate";
            return false;
        }
        else if (unwrapped is INamedTypeSymbol { Name: "IActionResult" })
        {
            skipReason = $"action '{method.Name}' returns IActionResult with no recoverable response type";
            return false;
        }
        else if (unwrapped is INamedTypeSymbol
                 {
                     Name: "FileResult" or "FileContentResult" or "FileStreamResult" or
                           "PhysicalFileResult" or "VirtualFileResult"
                 })
        {
            skipReason = $"action '{method.Name}' returns a file result, which RouteGen doesn't model (JSON or Stream response types only)";
            return false;
        }
        else if (unwrapped is INamedTypeSymbol
                 {
                     Name: "ContentResult" or "RedirectResult" or "RedirectToActionResult" or
                           "RedirectToRouteResult" or "EmptyResult" or "StatusCodeResult"
                 } named)
        {
            skipReason = $"action '{method.Name}' returns {named.Name}, a non-JSON result type RouteGen doesn't model";
            return false;
        }

        if (unwrapped is not null)
        {
            responseTypeFullName = unwrapped.ToDisplayString(FullyQualified);
            isStream = unwrapped.Name == "Stream";
        }

        return true;
    }

    /// <summary>
    /// Maps one action parameter to a <see cref="MigratedParameterKind"/>, replicating ASP.NET
    /// Core's own default binding-source inference for parameters with no explicit
    /// <c>[From*]</c> attribute: a name matching a route token binds from the route; otherwise a
    /// simple type binds from the query string; otherwise (a complex type with no attribute and
    /// no route-token match) MVC's real default is <c>[FromBody]</c>, so that's what's generated.
    /// </summary>
    private static bool TryMapParameter(
        IParameterSymbol param,
        HashSet<string> routeTokenNames,
        out ActionParameterMigrationModel? mapped,
        out string? skipReason)
    {
        skipReason = null;
        mapped = null;

        var type = param.Type;
        string typeFullName = type.ToDisplayString(FullyQualified);

        if (typeFullName == "global::System.Threading.CancellationToken")
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.CancellationToken);
            return true;
        }

        var attrs = param.GetAttributes();
        bool HasAttr(string simpleName) => attrs.Any(a => a.AttributeClass?.Name == simpleName);

        if (HasAttr("FromHeaderAttribute") || HasAttr("FromServicesAttribute"))
        {
            skipReason = $"has parameter '{param.Name}' using [FromHeader]/[FromServices], which RouteGen doesn't model";
            return false;
        }

        if (HasAttr("ModelBinderAttribute"))
        {
            skipReason = $"has parameter '{param.Name}' using a custom [ModelBinder], which RouteGen doesn't model";
            return false;
        }

        if (HasAttr("FromRouteAttribute"))
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Route);
            return true;
        }

        if (HasAttr("FromQueryAttribute"))
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Query);
            return true;
        }

        if (HasAttr("FromBodyAttribute"))
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Body);
            return true;
        }

        if (IsFormFileLike(type, out bool isMultiFile))
        {
            mapped = new ActionParameterMigrationModel(
                param.Name,
                isMultiFile ? "IReadOnlyList<FormFile>" : "FormFile",
                MigratedParameterKind.File)
            {
                IsMultiFile = isMultiFile,
            };
            return true;
        }

        if (HasAttr("FromFormAttribute"))
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Form);
            return true;
        }

        if (routeTokenNames.Contains(param.Name))
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Route);
            return true;
        }

        if (IsSimpleType(type))
        {
            mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Query);
            return true;
        }

        // Complex type, no attribute, no route-token match: MVC's real default is [FromBody].
        mapped = new ActionParameterMigrationModel(param.Name, typeFullName, MigratedParameterKind.Body);
        return true;
    }

    /// <summary>True for IFormFile, IFormFileCollection, or an array/common-collection-interface of IFormFile.</summary>
    private static bool IsFormFileLike(ITypeSymbol type, out bool isMulti)
    {
        isMulti = false;

        if (type is INamedTypeSymbol { Name: "IFormFile" }) return true;

        if (type is INamedTypeSymbol { Name: "IFormFileCollection" })
        {
            isMulti = true;
            return true;
        }

        if (type is IArrayTypeSymbol { ElementType: INamedTypeSymbol { Name: "IFormFile" } })
        {
            isMulti = true;
            return true;
        }

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named &&
            named.TypeArguments[0] is INamedTypeSymbol { Name: "IFormFile" } &&
            named.Name is "List" or "IEnumerable" or "ICollection" or "IList" or "IReadOnlyList" or "IReadOnlyCollection")
        {
            isMulti = true;
            return true;
        }

        return false;
    }

    /// <summary>True for primitives, string, enum, Guid, DateTime, and similar simple types -- the same rule RouteGen.Generators uses for [Query]/route parameters, duplicated here rather than referenced so this package has no dependency on Generators' internal (and unstable-shape) classes.</summary>
    private static bool IsSimpleType(ITypeSymbol type)
    {
        var underlying = type;

        if (underlying is INamedTypeSymbol { Name: "Nullable", IsGenericType: true } nullable)
            underlying = nullable.TypeArguments[0];

        if (underlying.TypeKind == TypeKind.Enum) return true;

        if (underlying.SpecialType is
            SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Byte or
            SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or
            SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or
            SpecialType.System_Decimal or SpecialType.System_Char)
        {
            return true;
        }

        return underlying.ToDisplayString(FullyQualified) is
            "global::System.Guid" or "global::System.DateTime" or "global::System.DateTimeOffset" or
            "global::System.TimeSpan" or "global::System.DateOnly" or "global::System.TimeOnly";
    }

    /// <summary>Reads an <c>[Authorize]</c> attribute, if present.</summary>
    private static (bool authorize, string? roles, string? policy) ReadAuthorize(
        System.Collections.Immutable.ImmutableArray<AttributeData> attributes)
    {
        var attr = attributes.FirstOrDefault(a => a.AttributeClass?.Name == "AuthorizeAttribute");
        if (attr is null) return (false, null, null);

        string? roles = null, policy = null;

        foreach (var na in attr.NamedArguments)
        {
            if (na.Key == "Roles") roles = na.Value.Value as string;
            if (na.Key == "Policy") policy = na.Value.Value as string;
        }

        return (true, roles, policy);
    }

    /// <summary>
    /// Collects the simple names of every type referenced by <paramref name="action"/> (its
    /// parameters and response type) that's declared in <paramref name="controllerAssembly"/> --
    /// i.e. likely private to the Server project today, flagged so the user knows it needs to
    /// move to Shared too.
    /// </summary>
    private static void CollectLocalTypes(
        ActionMigrationModel action, IAssemblySymbol controllerAssembly, List<string> localTypeNames)
    {
        foreach (var p in action.Parameters)
        {
            if (p.Kind is MigratedParameterKind.CancellationToken or MigratedParameterKind.File)
                continue; // CancellationToken is BCL; File's type is already rewritten to FormFile.

            CollectIfLocal(p.TypeFullName, controllerAssembly, localTypeNames);
        }

        if (action.ResponseTypeFullName is not null)
            CollectIfLocal(action.ResponseTypeFullName, controllerAssembly, localTypeNames);
    }

    /// <summary>Best-effort: strips common wrapper syntax (nullable "?", array "[]", one level of generic "&lt;T&gt;") to get at a plausible leaf type name, then checks whether the ORIGINAL fully-qualified text's namespace matches the controller's own assembly's default namespace as a heuristic. This is intentionally approximate -- see remarks in <see cref="MigratedInterfaceEmitter"/> on why it's a comment, not a build-breaking check.</summary>
    private static void CollectIfLocal(string typeFullName, IAssemblySymbol controllerAssembly, List<string> localTypeNames)
    {
        string leaf = typeFullName.TrimEnd('?').TrimEnd('[', ']');

        int genericStart = leaf.IndexOf('<');
        if (genericStart >= 0)
        {
            // Only look at the outer container for this heuristic; nested generic arguments
            // aren't walked further (kept deliberately simple -- see the emitter's remarks).
            leaf = leaf.Substring(0, genericStart);
        }

        int lastDot = leaf.LastIndexOf('.');
        string simpleName = lastDot >= 0 ? leaf.Substring(lastDot + 1) : leaf;

        if (simpleName.Length == 0) return;

        bool foundInControllerAssembly = controllerAssembly
            .GetTypeByMetadataName(leaf.Replace("global::", ""))
            is not null;

        if (foundInControllerAssembly && !localTypeNames.Contains(simpleName))
            localTypeNames.Add(simpleName);
    }

    /// <summary>Combines a base route and a suffix into a single template, normalizing slashes.</summary>
    private static string CombineRoutes(string baseRoute, string? suffix) =>
        string.IsNullOrEmpty(suffix)
            ? baseRoute
            : baseRoute.TrimEnd('/') + "/" + suffix!.TrimStart('/');

    /// <summary>Extracts every "{name}"/"{name:constraint}" token's name from a route template -- names only, not full parsing, since that's all parameter-kind inference needs.</summary>
    private static HashSet<string> ExtractRouteTokenNames(string route)
    {
        var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        int i = 0;

        while (i < route.Length)
        {
            if (route[i] == '{')
            {
                int close = route.IndexOf('}', i + 1);
                if (close < 0) break;

                string inner = route.Substring(i + 1, close - i - 1);
                i = close + 1;

                int cut = inner.IndexOfAny(new[] { ':', '?', '=' });
                string name = cut >= 0 ? inner.Substring(0, cut) : inner;

                if (name.Length > 0) names.Add(name);
            }
            else
            {
                i++;
            }
        }

        return names;
    }
}
