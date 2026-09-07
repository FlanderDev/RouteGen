using FlanderDev.RouteGen.Abstractions;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FlanderDev.RouteGen.Generators;

/// <summary>
/// Builds an <see cref="ApiInterfaceModel"/> from an <c>[ApiRoute]</c>-decorated interface symbol,
/// via the semantic model.
/// </summary>
internal static class ApiInterfaceReader
{
    /// <summary>Symbol display format producing fully-qualified, nullable-annotation-aware type names for generated code.</summary>
    private static readonly SymbolDisplayFormat FullyQualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>Special types considered safe to bind from a route segment or query string.</summary>
    private static readonly HashSet<SpecialType> SimpleSpecialTypes =
    [
        SpecialType.System_String, SpecialType.System_Boolean, SpecialType.System_Byte,
        SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64,
        SpecialType.System_UInt64, SpecialType.System_Single, SpecialType.System_Double,
        SpecialType.System_Decimal, SpecialType.System_Char,
    ];

    /// <summary>Parses <paramref name="interfaceSymbol"/> into an <see cref="ApiInterfaceModel"/>, or returns null if it isn't <see cref="ApiRouteAttribute"/>-decorated. Reports any RouteGen diagnostics found along the way into <paramref name="diagnostics"/>.</summary>
    public static ApiInterfaceModel? TryParse(
        INamedTypeSymbol interfaceSymbol,
        List<Diagnostic> diagnostics)
    {
        var apiRouteAttr = interfaceSymbol.GetAttributes()
            .FirstOrDefault(a => IsAttribute(a.AttributeClass, nameof(ApiRouteAttribute)));
        if (apiRouteAttr is null) return null;

        var model = new ApiInterfaceModel(
            @namespace: interfaceSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : interfaceSymbol.ContainingNamespace.ToDisplayString(),
            interfaceName: interfaceSymbol.Name,
            baseRoute: apiRouteAttr.ConstructorArguments.Length > 0
                ? apiRouteAttr.ConstructorArguments[0].Value as string ?? string.Empty
                : string.Empty);

        model.AbstractionsNamespace = apiRouteAttr.AttributeClass?.ContainingNamespace?.ToDisplayString()
            ?? model.AbstractionsNamespace;

        foreach (var namedArg in apiRouteAttr.NamedArguments)
        {
            if (namedArg.Key == nameof(ApiInterfaceModel.HttpClientName) && namedArg.Value.Value is string hc)
                model.HttpClientName = hc;
        }

        var (ifaceAuth, ifaceRoles, ifacePolicy) = ReadAuthorize(interfaceSymbol.GetAttributes());
        model.InterfaceLevelAuthorize = ifaceAuth;
        model.InterfaceLevelRoles = ifaceRoles;
        model.InterfaceLevelPolicy = ifacePolicy;

        foreach (var member in interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;

            var methodModel = ParseMethod(member, model, diagnostics);
            if (methodModel is not null)
                model.Methods.Add(methodModel);
        }

        DetectRouteCollisions(model, diagnostics);
        return model;
    }

    /// <summary>Reads and validates every attribute/parameter on an interface method, or returns null if it isn't an HTTP-verb-attributed operation.</summary>
    private static ApiMethodModel? ParseMethod(
        IMethodSymbol method,
        ApiInterfaceModel owner,
        List<Diagnostic> diagnostics)
    {
        HttpVerbInfo? verbInfo = null;

        foreach (var attr in method.GetAttributes())
        {
            string? verb = attr.AttributeClass?.Name switch
            {
                "GetAttribute" => "GET",
                "PostAttribute" => "POST",
                "PutAttribute" => "PUT",
                "DeleteAttribute" => "DELETE",
                "PatchAttribute" => "PATCH",
                _ => null
            };

            if (verb is null) continue;

            string? suffix = attr.ConstructorArguments.Length > 0
                ? attr.ConstructorArguments[0].Value as string
                : null;

            verbInfo = new HttpVerbInfo(verb, suffix);
            break;
        }

        if (verbInfo is null)
            return null;

        var (methodAuth, roles, policy) = ReadAuthorize(method.GetAttributes());
        bool allowAnonymous = method.GetAttributes()
            .Any(a => IsAttribute(a.AttributeClass, nameof(AllowAnonymousAttribute)));

        string combinedTemplate = RouteTemplateParser.Combine(owner.BaseRoute, verbInfo.Value.Suffix);
        RouteTemplate routeTemplate = RouteTemplateParser.Parse(combinedTemplate);

        var methodModel = new ApiMethodModel(
            method.Name,
            verbInfo.Value.Verb,
            verbInfo.Value.Suffix,
            routeTemplate)
        {
            HasAuthorize = methodAuth && !allowAnonymous,
            Roles = roles,
            Policy = policy,
            AllowAnonymous = allowAnonymous,
        };

        if (!methodAuth && !allowAnonymous && owner.InterfaceLevelAuthorize)
        {
            methodModel.HasAuthorize = true;
            methodModel.Roles = owner.InterfaceLevelRoles;
            methodModel.Policy = owner.InterfaceLevelPolicy;
        }

        // Return type
        if (method.ReturnType is INamedTypeSymbol { Name: "Task" } taskType)
        {
            if (taskType.IsGenericType)
            {
                var arg = taskType.TypeArguments[0];
                methodModel.ResponseTypeFullName = arg.ToDisplayString(FullyQualified);
                methodModel.IsResponseNullable = IsNullableType(arg, null);
                methodModel.IsStreamResponse = arg.Name == "Stream";
            }
            else
            {
                methodModel.ResponseTypeFullName = null;
            }
        }
        else
        {
            methodModel.ResponseTypeFullName = method.ReturnType.ToDisplayString(FullyQualified);
        }

        var tokens = routeTemplate.Parameters;
        var matchedTokenNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        bool sawBody = false;

        foreach (var param in method.Parameters)
        {
            var paramType = param.Type;
            bool isCancellationToken =
                paramType.ToDisplayString(FullyQualified) == "global::System.Threading.CancellationToken";

            var paramModel = new ApiParameterModel(
                param.Name,
                paramType.ToDisplayString(FullyQualified))
            {
                IsNullable = IsNullableType(paramType, param),
                HasDefaultValue = param.HasExplicitDefaultValue,
                DefaultValueLiteral = param.HasExplicitDefaultValue ? FormatDefault(param) : null,
            };

            if (isCancellationToken)
            {
                paramModel.Kind = ParameterKind.CancellationToken;
                methodModel.Parameters.Add(paramModel);
                continue;
            }

            bool isQuery = param.GetAttributes()
                .Any(a => IsAttribute(a.AttributeClass, nameof(QueryAttribute)));

            bool isBody = param.GetAttributes()
                .Any(a => IsAttribute(a.AttributeClass, nameof(BodyAttribute)));

            bool isForm = param.GetAttributes()
                .Any(a => IsAttribute(a.AttributeClass, nameof(FormAttribute)));

            bool isFile = param.GetAttributes()
                .Any(a => IsAttribute(a.AttributeClass, nameof(FileAttribute)));

            var routeOverride = param.GetAttributes()
                .FirstOrDefault(a => IsAttribute(a.AttributeClass, nameof(RouteAttribute)));

            string? overrideTokenName = null;
            if (routeOverride is not null && routeOverride.ConstructorArguments.Length > 0)
                overrideTokenName = routeOverride.ConstructorArguments[0].Value as string;

            string tokenNameToMatch = overrideTokenName ?? param.Name;

            var matchedToken = tokens.FirstOrDefault(t =>
                string.Equals(t.Name, tokenNameToMatch, System.StringComparison.OrdinalIgnoreCase));

            bool matches = matchedToken is not null;

            if (isBody)
            {
                if (sawBody)
                    diagnostics.Add(Diagnostic.Create(
                        RouteGenDiagnostics.MultipleBodyParameters,
                        GetLocation(method),
                        method.Name));

                sawBody = true;
                paramModel.Kind = ParameterKind.Body;

                if (methodModel.Verb is "GET" or "DELETE")
                {
                    diagnostics.Add(Diagnostic.Create(
                        RouteGenDiagnostics.BodyOnNonBodyVerb,
                        GetLocation(param),
                        param.Name,
                        method.Name,
                        methodModel.Verb));
                }
            }
            else if (isQuery)
            {
                paramModel.Kind = ParameterKind.Query;
                CheckSimpleType(paramType, param, method, diagnostics);
            }
            else if (isFile)
            {
                paramModel.Kind = ParameterKind.File;

                if (TryGetFileParameterShape(paramType, out bool isMultiFile, out string? serverTypeFullName))
                {
                    paramModel.IsMultiFile = isMultiFile;
                    paramModel.ServerFileTypeFullName = serverTypeFullName;
                }
                else
                {
                    diagnostics.Add(Diagnostic.Create(
                        RouteGenDiagnostics.InvalidFileParameterType,
                        GetLocation(param),
                        param.Name,
                        method.Name,
                        paramType.ToDisplayString()));
                }
            }
            else if (isForm)
            {
                paramModel.Kind = ParameterKind.Form;
                // No type restriction here (unlike [Query]/route parameters, which still go
                // through CheckSimpleType/RG0006): a simple-typed field is sent as a plain
                // string; anything else is JSON-serialized into the field instead. IsSimpleType
                // just records which path the emitters should take -- it is never a reason to
                // reject the parameter for [Form].
                paramModel.IsSimpleType = IsSimpleType(paramType);
            }
            else if (matches)
            {
                paramModel.Kind = ParameterKind.RouteOrAuto;
                paramModel.MatchesRouteToken = true;
                paramModel.RouteTokenNameOverride = overrideTokenName;
                paramModel.RouteConstraint = matchedToken!.Constraint;
                matchedTokenNames.Add(tokenNameToMatch);
                CheckSimpleType(paramType, param, method, diagnostics);
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(
                    RouteGenDiagnostics.UnmatchedParameter,
                    GetLocation(param),
                    param.Name,
                    method.Name));

                paramModel.Kind = ParameterKind.Query;
            }

            methodModel.Parameters.Add(paramModel);
        }

        foreach (var token in tokens)
        {
            if (!matchedTokenNames.Contains(token.Name) &&
                !methodModel.Parameters.Any(p =>
                    p.MatchesRouteToken &&
                    string.Equals(
                        p.RouteTokenNameOverride ?? p.Name,
                        token.Name,
                        System.StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(Diagnostic.Create(
                    RouteGenDiagnostics.UnmatchedRouteToken,
                    GetLocation(method),
                    combinedTemplate,
                    method.Name,
                    token.Name));
            }
        }

        if (methodModel.Parameters.Any(p => p.Kind == ParameterKind.Body) &&
            methodModel.Parameters.Any(p => p.Kind is ParameterKind.Form or ParameterKind.File))
        {
            diagnostics.Add(Diagnostic.Create(
                RouteGenDiagnostics.MixedBodyAndMultipart,
                GetLocation(method),
                method.Name));
        }

        return methodModel;
    }

    /// <summary>
    /// True when <paramref name="type"/> is a valid <c>[File]</c> parameter shape, in which case
    /// <paramref name="serverTypeFullName"/> is the exact fully-qualified server-side type to
    /// generate (the same shape as <paramref name="type"/>, with <c>IFormFile</c> substituted for
    /// <c>FormFile</c>/<c>FormFile&lt;TMetadata&gt;</c>).
    ///
    /// A single file is always safe. For multiple files, only shapes ASP.NET Core's own model
    /// binder is verified to construct without throwing are accepted -- confirmed against
    /// <c>ModelBindingHelper.GetCompatibleCollection&lt;T&gt;</c>
    /// (https://github.com/dotnet/aspnetcore/blob/main/src/Mvc/Mvc.Core/src/ModelBinding/ModelBindingHelper.cs),
    /// which the framework's own <c>FormFileModelBinder</c> uses for every multi-file parameter:
    ///   - <c>T[]</c> -- always works (a <c>List&lt;T&gt;</c> is bound, then copied to an array).
    ///   - Any of <c>IEnumerable&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
    ///     <c>IReadOnlyCollection&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, or <c>List&lt;T&gt;</c>
    ///     itself -- always works (a <c>List&lt;T&gt;</c> is bound and assigned directly, since
    ///     <c>List&lt;T&gt;</c> is assignable to every one of these).
    ///   - Any OTHER concrete, non-abstract, generic collection class closed over
    ///     <c>FormFile</c>/<c>FormFile&lt;TMetadata&gt;</c> -- works ONLY if it has an accessible
    ///     public parameterless constructor AND implements <c>ICollection&lt;T&gt;</c>, since the
    ///     binder falls back to <c>(ICollection&lt;T&gt;)Activator.CreateInstance(modelType)</c>
    ///     for anything that isn't one of the shapes above. Types that satisfy
    ///     <c>ICollection&lt;T&gt;</c> assignability but lack a public parameterless constructor
    ///     (e.g. <c>ImmutableList&lt;T&gt;</c>, which has no public constructor at all) pass
    ///     ASP.NET Core's own <c>CanGetCompatibleCollection&lt;T&gt;</c> check but then throw
    ///     <c>MissingMethodException</c> from <c>GetCompatibleCollection&lt;T&gt;</c> at request
    ///     time -- RouteGen checks the constructor explicitly so this fails at compile time
    ///     instead, as RG0010, rather than reproducing that runtime landmine.
    ///   - A non-generic concrete collection type hardcoded to a <c>FormFile</c> element type
    ///     (e.g. a hand-written <c>class Gallery : List&lt;FormFile&gt;</c>) can never be
    ///     mirrored -- there is no way to construct an analogous type closed over <c>IFormFile</c>
    ///     instead, so these are always rejected (RG0010).
    /// </summary>
    private static bool TryGetFileParameterShape(ITypeSymbol type, out bool isMultiFile, out string? serverTypeFullName)
    {
        const string ServerElementType = "global::Microsoft.AspNetCore.Http.IFormFile";

        isMultiFile = false;
        serverTypeFullName = null;

        if (IsFormFileType(type))
        {
            serverTypeFullName = ServerElementType;
            return true;
        }

        if (type is IArrayTypeSymbol arrayType && IsFormFileType(arrayType.ElementType))
        {
            isMultiFile = true;
            serverTypeFullName = ServerElementType + "[]";
            return true;
        }

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named &&
            IsFormFileType(named.TypeArguments[0]))
        {
            isMultiFile = true;
            string containerName = "global::" + named.ContainingNamespace.ToDisplayString() + "." + named.Name;

            bool isListAssignableShape = named.Name is
                "List" or "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList";

            if (isListAssignableShape)
            {
                serverTypeFullName = containerName + "<" + ServerElementType + ">";
                return true;
            }

            // Anything else: only safe if ASP.NET Core can actually Activator.CreateInstance it
            // and treat it as an ICollection<T> -- see the constructor/interface checks below.
            bool hasPublicParameterlessCtor = named.InstanceConstructors
                .Any(c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public);
            bool implementsMatchingICollection = named.AllInterfaces.Any(i =>
                i is { Name: "ICollection", IsGenericType: true, TypeArguments.Length: 1 } &&
                IsFormFileType(i.TypeArguments[0]));

            if (named.TypeKind == TypeKind.Class && !named.IsAbstract &&
                hasPublicParameterlessCtor && implementsMatchingICollection)
            {
                serverTypeFullName = containerName + "<" + ServerElementType + ">";
            return true;
        }

            isMultiFile = false;
            return false;
        }

        return false;
    }

    /// <summary>True when <paramref name="type"/> is <see cref="FormFile"/> or a closed <see cref="FormFile{TMetadata}"/>.</summary>
    private static bool IsFormFileType(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "FormFile", Arity: 0 or 1 } named &&
        named.ContainingNamespace?.ToDisplayString() == "FlanderDev.RouteGen.Abstractions";

    /// <summary>
    /// True when <paramref name="type"/> can hold null at runtime: a nullable reference type, a
    /// nullable value type (<c>Nullable&lt;T&gt;</c>), or -- defensively, since <c>default</c>
    /// means null for those two cases just as much as it means zero for a plain value type -- a
    /// parameter whose declared default value is literally null.
    /// </summary>
    private static bool IsNullableType(ITypeSymbol type, IParameterSymbol? param)
    {
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
            return true;

        if (type is INamedTypeSymbol { Name: "Nullable", IsGenericType: true })
            return true;

        if (param is not null && param.HasExplicitDefaultValue && param.ExplicitDefaultValue is null)
            return true;

        return false;
    }

    /// <summary>True when <paramref name="type"/> (unwrapping <c>Nullable&lt;T&gt;</c> first) is a route/query-safe simple type: a primitive, string, enum, Guid, DateTime, or similar.</summary>
    private static bool IsSimpleType(ITypeSymbol type)
    {
        var underlying = type;

        if (underlying is INamedTypeSymbol { Name: "Nullable", IsGenericType: true } nullable)
            underlying = nullable.TypeArguments[0];

        return underlying.TypeKind == TypeKind.Enum
            || SimpleSpecialTypes.Contains(underlying.SpecialType)
            || underlying.ToDisplayString(FullyQualified) is
                "global::System.Guid" or "global::System.DateTime" or "global::System.DateTimeOffset"
                or "global::System.TimeSpan" or "global::System.DateOnly" or "global::System.TimeOnly";
    }

    /// <summary>Reports <see cref="RouteGenDiagnostics.UnsupportedSimpleType"/> unless <see cref="IsSimpleType"/> is true for <paramref name="type"/>. Used for route/query parameters only -- <c>[Form]</c> has no such restriction, see <see cref="ApiParameterModel.IsSimpleType"/>.</summary>
    private static void CheckSimpleType(
        ITypeSymbol type,
        IParameterSymbol param,
        IMethodSymbol method,
        List<Diagnostic> diagnostics)
    {
        if (!IsSimpleType(type))
        {
            diagnostics.Add(Diagnostic.Create(
                RouteGenDiagnostics.UnsupportedSimpleType,
                GetLocation(param),
                param.Name,
                method.Name,
                type.ToDisplayString()));
        }
    }

    /// <summary>True when <paramref name="attributeType"/> is (or is named like) <paramref name="simpleName"/>, tolerating attributes from any namespace.</summary>
    private static bool IsAttribute(INamedTypeSymbol? attributeType, string simpleName)
        => attributeType is not null &&
           (attributeType.Name == simpleName ||
            attributeType.ToDisplayString().EndsWith("." + simpleName, System.StringComparison.Ordinal));

    /// <summary>Reports <see cref="RouteGenDiagnostics.RouteCollision"/> for any two methods on <paramref name="model"/> that resolve to the same verb and route.</summary>
    private static void DetectRouteCollisions(
         ApiInterfaceModel model,
         List<Diagnostic> diagnostics)
    {
        var seen = new Dictionary<string, ApiMethodModel>();

        foreach (var m in model.Methods)
        {
            string route = RouteTemplateParser.Combine(model.BaseRoute, m.RouteSuffix);
            string key = m.Verb + " " + route.ToLowerInvariant();

            if (seen.TryGetValue(key, out var existing))
            {
                diagnostics.Add(Diagnostic.Create(
                    RouteGenDiagnostics.RouteCollision,
                    Location.None,
                    existing.Name,
                    m.Name,
                    model.InterfaceName,
                    m.Verb,
                    route));
            }
            else
            {
                seen[key] = m;
            }
        }
    }

    /// <summary>Reads an <see cref="AuthorizeAttribute"/> from <paramref name="attributes"/>, if present.</summary>
    private static (bool authorize, string? roles, string? policy) ReadAuthorize(
        ImmutableArray<AttributeData> attributes)
    {
        var attr = attributes.FirstOrDefault(
            a => IsAttribute(a.AttributeClass, nameof(AuthorizeAttribute)));

        if (attr is null) return (false, null, null);

        string? roles = null;
        string? policy = null;

        foreach (var na in attr.NamedArguments)
        {
            if (na.Key == "Roles") roles = na.Value.Value as string;
            if (na.Key == "Policy") policy = na.Value.Value as string;
        }

        return (true, roles, policy);
    }

    /// <summary>Formats a parameter's declared default value as a C# literal usable in generated source, handling null, enum, string, bool, and char specially.</summary>
    private static string? FormatDefault(IParameterSymbol param)
    {
        if (!param.HasExplicitDefaultValue) return null;

        var value = param.ExplicitDefaultValue;
        if (value is null) return "default"; // for reference types AND Nullable<T>


        // Get underlying type for nullable parameters, so enum defaults are handled correctly.
        var underlyingType = param.Type is INamedTypeSymbol { Name: "Nullable", IsGenericType: true } n
            ? n.TypeArguments[0]
            : param.Type;

        if (underlyingType.TypeKind == TypeKind.Enum)
        {
            // An enum cannot (implicitly) be assinged an integer literal as its default (excpet 0... don't ask).
            // Prefer the matching enum member when there is one, and otherwise emit an explicit cast.
            // The cast also handles values such as combined [Flags] values that have no named member.
            var enumType = (INamedTypeSymbol)underlyingType;
            var matchingMember = enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            string enumTypeName = underlyingType.ToDisplayString(FullyQualified);
            return matchingMember is not null
                ? $"{enumTypeName}.{matchingMember.Name}"
                : $"({enumTypeName}){value}";
        }

        if (value is string s) return $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        if (value is bool b) return b ? "true" : "false";
        if (value is char c) return $"'{c}'";
        return value.ToString();
    }

    /// <summary>The first source location for <paramref name="symbol"/>, or <see cref="Location.None"/> if it has none.</summary>
    private static Location GetLocation(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault() ?? Location.None;

    /// <summary>The HTTP verb and optional route suffix parsed from a method's verb attribute.</summary>
    private readonly struct HttpVerbInfo(string verb, string? suffix)
    {
        /// <summary>The HTTP verb, e.g. "GET".</summary>
        public string Verb { get; } = verb;

        /// <summary>The route template suffix, if any.</summary>
        public string? Suffix { get; } = suffix;
    }
}
