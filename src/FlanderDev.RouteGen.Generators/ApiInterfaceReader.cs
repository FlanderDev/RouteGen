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
    private static readonly SymbolDisplayFormat FullyQualified =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly HashSet<SpecialType> SimpleSpecialTypes =
    [
        SpecialType.System_String, SpecialType.System_Boolean, SpecialType.System_Byte,
        SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64,
        SpecialType.System_UInt64, SpecialType.System_Single, SpecialType.System_Double,
        SpecialType.System_Decimal, SpecialType.System_Char,
    ];

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

                if (TryGetFileParameterShape(paramType, out bool isMultiFile))
                {
                    paramModel.IsMultiFile = isMultiFile;
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
                CheckSimpleType(paramType, param, method, diagnostics);
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
    /// Determines whether a type is a valid <see cref="FileAttribute"/> parameter type: either a
    /// single <c>FormFile</c>, or a list-like collection of them (<c>IReadOnlyList&lt;FormFile&gt;</c>,
    /// <c>IEnumerable&lt;FormFile&gt;</c>, <c>IList&lt;FormFile&gt;</c>, <c>List&lt;FormFile&gt;</c>,
    /// <c>ICollection&lt;FormFile&gt;</c>, or <c>FormFile[]</c>), optionally nullable either way
    /// (nullability itself is tracked separately via <see cref="IsNullableType"/>).
    /// </summary>
    private static bool TryGetFileParameterShape(ITypeSymbol type, out bool isMultiFile)
    {
        isMultiFile = false;

        if (IsFormFileType(type))
            return true;

        if (type is IArrayTypeSymbol arrayType && IsFormFileType(arrayType.ElementType))
        {
            isMultiFile = true;
            return true;
        }

        if (type is INamedTypeSymbol { IsGenericType: true } named &&
            named.TypeArguments.Length == 1 &&
            IsFormFileType(named.TypeArguments[0]) &&
            named.Name is "IReadOnlyList" or "IEnumerable" or "IList" or "List"
                or "ICollection" or "IReadOnlyCollection")
        {
            isMultiFile = true;
            return true;
        }

        return false;
    }

    private static bool IsFormFileType(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "FormFile" };

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

    private static void CheckSimpleType(
        ITypeSymbol type,
        IParameterSymbol param,
        IMethodSymbol method,
        List<Diagnostic> diagnostics)
    {
        var underlying = type;

        if (underlying is INamedTypeSymbol { Name: "Nullable", IsGenericType: true } nullable)
            underlying = nullable.TypeArguments[0];

        bool ok = underlying.TypeKind == TypeKind.Enum
            || SimpleSpecialTypes.Contains(underlying.SpecialType)
            || underlying.ToDisplayString(FullyQualified) is
                "global::System.Guid" or "global::System.DateTime" or "global::System.DateTimeOffset"
                or "global::System.TimeSpan" or "global::System.DateOnly" or "global::System.TimeOnly";

        if (!ok)
        {
            diagnostics.Add(Diagnostic.Create(
                RouteGenDiagnostics.UnsupportedSimpleType,
                GetLocation(param),
                param.Name,
                method.Name,
                type.ToDisplayString()));
        }
    }

    private static bool IsAttribute(INamedTypeSymbol? attributeType, string simpleName)
        => attributeType is not null &&
           (attributeType.Name == simpleName ||
            attributeType.ToDisplayString().EndsWith("." + simpleName, System.StringComparison.Ordinal));

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

    private static Location GetLocation(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault() ?? Location.None;

    private readonly struct HttpVerbInfo(string verb, string? suffix)
    {
        public string Verb { get; } = verb;
        public string? Suffix { get; } = suffix;
    }
}
