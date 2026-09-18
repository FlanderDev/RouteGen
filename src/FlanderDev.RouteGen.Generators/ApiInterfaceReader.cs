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

    /// <summary>
    /// Parses <paramref name="interfaceSymbol"/> into an <see cref="ApiInterfaceModel"/>, or
    /// returns null if it isn't <see cref="ApiRouteAttribute"/>-decorated. Reports any RouteGen
    /// diagnostics found along the way into <paramref name="diagnostics"/>.
    /// </summary>
    /// <param name="interfaceSymbol">The candidate interface symbol.</param>
    /// <param name="formFileType">
    /// The resolved <c>Microsoft.AspNetCore.Http.IFormFile</c> symbol, when available (i.e. this
    /// is the server compilation). Used only for RG0011's generic-constraint check on custom
    /// <c>[File]</c> collection types; pass null (e.g. from Shared/Client, which have no ASP.NET
    /// Core reference at all) to skip that specific check rather than guess at it.
    /// </param>
    /// <param name="diagnostics">Diagnostics discovered while parsing are appended here.</param>
    public static ApiInterfaceModel? TryParse(
        INamedTypeSymbol interfaceSymbol,
        INamedTypeSymbol? formFileType,
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

            var methodModel = ParseMethod(member, model, formFileType, diagnostics);
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
        INamedTypeSymbol? formFileType,
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
                IsCultureSensitive = IsCultureSensitiveType(paramType),
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

                if (TryGetFileParameterShape(
                        paramType, formFileType,
                        out bool isMultiFile, out bool isFileWithData, out string? fileWithDataTypeFullName,
                        out string? serverTypeFullName, out string? constraintIssue))
                {
                    paramModel.IsMultiFile = isMultiFile;
                    paramModel.IsFileWithData = isFileWithData;
                    paramModel.FileWithDataTypeFullName = fileWithDataTypeFullName;

                    if (constraintIssue is not null)
                    {
                        diagnostics.Add(Diagnostic.Create(
                            RouteGenDiagnostics.IncompatibleFileCollectionConstraint,
                            GetLocation(param),
                            param.Name,
                            method.Name,
                            paramType.ToDisplayString(),
                            constraintIssue));

                        // Same "one clean diagnostic, no cascading raw compiler error" principle
                        // as the RG0010 fallback just below: don't emit the (invalid) mirrored
                        // type, echo back the client type instead.
                        paramModel.ServerFileTypeFullName = paramModel.TypeFullName;
                    }
                    else
                    {
                        paramModel.ServerFileTypeFullName = serverTypeFullName;
                    }
                }
                else
                {
                    diagnostics.Add(Diagnostic.Create(
                        RouteGenDiagnostics.InvalidFileParameterType,
                        GetLocation(param),
                        param.Name,
                        method.Name,
                        paramType.ToDisplayString()));

                    // RG0010 above already fails the build with a clear message; still fall back
                    // to a syntactically valid (if semantically wrong) server type here, echoing
                    // back exactly the type the interface declared, so the invalid [File]
                    // parameter doesn't ALSO surface a second, confusing raw syntax error (an
                    // empty/missing parameter type) alongside the one clear diagnostic.
                    paramModel.ServerFileTypeFullName = paramModel.TypeFullName;
                }
            }
            else if (isForm)
            {
                paramModel.Kind = ParameterKind.Form;
                // No type restriction here (unlike [Query]/route parameters, which still go
                // through CheckSimpleType/RG0006): a simple-typed field is sent as a plain
                // string; anything else is JSON-serialized into the field instead. IsSimpleType
                // just records which path the emitters should take, it is never a reason to
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

        AssignCancellationTokenDefault(methodModel);

        return methodModel;
    }

    /// <summary>
    /// Gives a <see cref="ParameterKind.CancellationToken"/> parameter a <c>= default</c> value
    ///, but only when doing so can't push some later, still-required parameter into an invalid
    /// "required after optional" position (CS1737). Scans right to left so the decision accounts
    /// for every parameter that follows, not just the immediate next one; a CancellationToken
    /// with a genuinely required parameter after it (itself already legal C#, since neither has
    /// a default) is left exactly as declared, so the generated signature's ordering always
    /// matches an ordering the interface method itself already legally compiled with.
    /// </summary>
    private static void AssignCancellationTokenDefault(ApiMethodModel methodModel)
    {
        bool trailingRequired = false;

        for (int i = methodModel.Parameters.Count - 1; i >= 0; i--)
        {
            var p = methodModel.Parameters[i];

            if (p.Kind == ParameterKind.CancellationToken && !trailingRequired && !p.HasDefaultValue)
            {
                p.HasDefaultValue = true;
                p.DefaultValueLiteral = "default";
            }

            if (!p.HasDefaultValue)
                trailingRequired = true;
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> is a valid <c>[File]</c> parameter shape, in which case
    /// <paramref name="serverTypeFullName"/> is the exact server-side type to generate (the same
    /// shape as <paramref name="type"/>, with <c>IFormFile</c> substituted for <c>FormFile</c>,
    /// or the generated controller base's own nested <c>FileWithData&lt;TData&gt;</c> substituted
    /// for <c>FileWithData&lt;TData&gt;</c>).
    ///
    /// A single file (or file+data pair) is always safe. For multiple files, only shapes ASP.NET
    /// Core's own model binder is verified to construct without throwing are accepted, confirmed
    /// against <c>ModelBindingHelper.GetCompatibleCollection&lt;T&gt;</c>
    /// (https://github.com/dotnet/aspnetcore/blob/main/src/Mvc/Mvc.Core/src/ModelBinding/ModelBindingHelper.cs),
    /// which the framework's own <c>FormFileModelBinder</c> uses for every multi-file parameter:
    ///   - <c>T[]</c>, always works (a <c>List&lt;T&gt;</c> is bound, then copied to an array).
    ///   - Any of <c>IEnumerable&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
    ///     <c>IReadOnlyCollection&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, or <c>List&lt;T&gt;</c>
    ///     itself, always works (a <c>List&lt;T&gt;</c> is bound and assigned directly, since
    ///     <c>List&lt;T&gt;</c> is assignable to every one of these).
    ///   - Any OTHER concrete, non-abstract, generic collection class closed over
    ///     <c>FormFile</c>, works ONLY if it has an accessible public parameterless constructor
    ///     AND implements <c>ICollection&lt;T&gt;</c>, since the binder falls back to
    ///     <c>(ICollection&lt;T&gt;)Activator.CreateInstance(modelType)</c> for anything that
    ///     isn't one of the shapes above. Types that satisfy <c>ICollection&lt;T&gt;</c>
    ///     assignability but lack a public parameterless constructor (e.g. <c>ImmutableList&lt;T&gt;</c>,
    ///     which has no public constructor at all) pass ASP.NET Core's own
    ///     <c>CanGetCompatibleCollection&lt;T&gt;</c> check but then throw
    ///     <c>MissingMethodException</c> from <c>GetCompatibleCollection&lt;T&gt;</c> at request
    ///     time, RouteGen checks the constructor explicitly so this fails at compile time
    ///     instead, as RG0010, rather than reproducing that runtime landmine. This custom-collection
    ///     path applies identically whether the element type is <c>FormFile</c> or
    ///     <c>FileWithData&lt;TData&gt;</c>, EXCEPT the RG0011 generic-constraint check (see
    ///     <see cref="TryFindIncompatibleConstraint"/>), which only runs for a <c>FormFile</c>
    ///     element, extending it to a <c>FileWithData&lt;TData&gt;</c> element (a custom
    ///     collection AND a paired-data file AND an incompatible generic constraint, all at once)
    ///     was judged too narrow an edge case to justify the added complexity for v1.
    ///   - A non-generic concrete collection type hardcoded to a <c>FormFile</c> element type
    ///     (e.g. a hand-written <c>class Gallery : List&lt;FormFile&gt;</c>) can never be
    ///     mirrored, there is no way to construct an analogous type closed over <c>IFormFile</c>
    ///     instead, so these are always rejected (RG0010).
    /// </summary>
    private static bool TryGetFileParameterShape(
        ITypeSymbol type,
        INamedTypeSymbol? formFileType,
        out bool isMultiFile,
        out bool isFileWithData,
        out string? fileWithDataTypeFullName,
        out string? serverTypeFullName,
        out string? constraintIncompatibilityReason)
    {
        const string FormFileServerType = "global::Microsoft.AspNetCore.Http.IFormFile";

        isMultiFile = false;
        isFileWithData = false;
        fileWithDataTypeFullName = null;
        serverTypeFullName = null;
        constraintIncompatibilityReason = null;

        // Single element (not a collection): FormFile or FileWithData<TData>.
        if (IsFormFileType(type))
        {
            serverTypeFullName = FormFileServerType;
            return true;
        }

        if (IsFileWithDataType(type, out var singleDataType))
        {
            isFileWithData = true;
            fileWithDataTypeFullName = singleDataType!.ToDisplayString(FullyQualified);
            serverTypeFullName = "FileWithData<" + fileWithDataTypeFullName + ">";
            return true;
        }

        ITypeSymbol? elementType = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named => named.TypeArguments[0],
            _ => null
        };

        if (elementType is null) return false;

        bool elementIsFormFile = IsFormFileType(elementType);
        bool elementIsFileWithDataType = IsFileWithDataType(elementType, out var collectionDataType);
        bool elementIsFileWithData = !elementIsFormFile && elementIsFileWithDataType;

        if (!elementIsFormFile && !elementIsFileWithData) return false;

            isMultiFile = true;
        isFileWithData = elementIsFileWithData;

        string elementServerType;
        if (elementIsFormFile)
        {
            elementServerType = FormFileServerType;
        }
        else
        {
            fileWithDataTypeFullName = collectionDataType!.ToDisplayString(FullyQualified);
            elementServerType = "FileWithData<" + fileWithDataTypeFullName + ">";
        }

        if (type is IArrayTypeSymbol)
        {
            serverTypeFullName = elementServerType + "[]";
            return true;
        }

        var namedCollection = (INamedTypeSymbol)type;
        string containerName = "global::" + namedCollection.ContainingNamespace.ToDisplayString() + "." + namedCollection.Name;

            // Namespace-checked, not just simple-name-checked: a user's own type coincidentally
            // named e.g. "List" or "IReadOnlyList" in some other namespace is NOT guaranteed
            // assignable the way the real System.Collections.Generic shapes are, and must instead
            // go through the constructor/ICollection<T> verification below like any other custom
            // collection type.
            bool isListAssignableShape =
            namedCollection.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" &&
            namedCollection.Name is "List" or "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList";

            if (isListAssignableShape)
            {
            serverTypeFullName = containerName + "<" + elementServerType + ">";
                return true;
            }

            // Anything else: only safe if ASP.NET Core can actually Activator.CreateInstance it
            // and treat it as an ICollection<T>, see the constructor/interface checks below.
        bool hasPublicParameterlessCtor = namedCollection.InstanceConstructors
                .Any(c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public);
        bool implementsMatchingICollection = namedCollection.AllInterfaces.Any(i =>
                i is { Name: "ICollection", IsGenericType: true, TypeArguments.Length: 1 } &&
            (elementIsFormFile ? IsFormFileType(i.TypeArguments[0]) : IsFileWithDataType(i.TypeArguments[0], out _)));

        if (namedCollection.TypeKind == TypeKind.Class && !namedCollection.IsAbstract &&
                hasPublicParameterlessCtor && implementsMatchingICollection)
            {
                // RG0011: even though the shape is otherwise valid, substituting IFormFile in for
                // this custom collection's type parameter could still be an invalid closed generic
                // type server-side (e.g. a plausible `where T : FormFile` constraint), only
            // checked when IFormFile itself is resolvable (the server compilation) and only for
            // a FormFile element (see this method's own doc comment for why FileWithData<TData>
            // elements skip this check for now).
            if (elementIsFormFile && formFileType is not null)
                TryFindIncompatibleConstraint(namedCollection, formFileType, out constraintIncompatibilityReason);

            serverTypeFullName = containerName + "<" + elementServerType + ">";
            return true;
        }

            isMultiFile = false;
        isFileWithData = false;
        fileWithDataTypeFullName = null;
        return false;
    }

    /// <summary>
    /// Checks whether <paramref name="formFileType"/> (IFormFile) would actually satisfy the
    /// generic constraints on <paramref name="named"/>'s (single) type parameter. A plausible
    /// <c>where T : FormFile</c> on a custom collection type would make the mirrored
    /// <c>MyBag&lt;IFormFile&gt;</c> an invalid closed generic type, without this check, that
    /// would surface as a confusing raw generic-constraint compiler error in the generated server
    /// file, rather than a clear, RouteGen-specific diagnostic (RG0011) pointing at the actual
    /// interface method that declared the incompatible <c>[File]</c> parameter.
    /// </summary>
    private static bool TryFindIncompatibleConstraint(
        INamedTypeSymbol named, INamedTypeSymbol formFileType, out string? reason)
    {
        reason = null;
        var typeParam = named.OriginalDefinition.TypeParameters.FirstOrDefault();
        if (typeParam is null) return false;

        if (typeParam.HasValueTypeConstraint)
        {
            reason = "a value-type constraint ('struct'), which the interface IFormFile can never satisfy";
            return true;
        }

        if (typeParam.HasConstructorConstraint)
        {
            reason = "a constructor constraint ('new()'), which the interface IFormFile can never satisfy";
            return true;
        }

        foreach (var constraintType in typeParam.ConstraintTypes)
        {
            if (constraintType.SpecialType == SpecialType.System_Object) continue;

            bool satisfied =
                SymbolEqualityComparer.Default.Equals(constraintType, formFileType) ||
                formFileType.AllInterfaces.Contains(constraintType, SymbolEqualityComparer.Default);

            if (!satisfied)
            {
                reason = "a 'where T : " + constraintType.ToDisplayString() +
                    "' constraint, which the interface IFormFile does not satisfy";
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="type"/> is (specifically) <see cref="FlanderDev.RouteGen.Abstractions.FormFile"/>, not any other type that happens to share its name.</summary>
    private static bool IsFormFileType(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "FormFile", Arity: 0 } named &&
        named.ContainingNamespace?.ToDisplayString() == "FlanderDev.RouteGen.Abstractions";

    /// <summary>True when <paramref name="type"/> is a closed <see cref="FlanderDev.RouteGen.Abstractions.FileWithData{TData}"/>, in which case <paramref name="dataType"/> is its <c>TData</c> type argument.</summary>
    private static bool IsFileWithDataType(ITypeSymbol type, out ITypeSymbol? dataType)
    {
        if (type is INamedTypeSymbol { Name: "FileWithData", Arity: 1 } named &&
            named.ContainingNamespace?.ToDisplayString() == "FlanderDev.RouteGen.Abstractions")
        {
            dataType = named.TypeArguments[0];
            return true;
        }

        dataType = null;
        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> can hold null at runtime: a nullable reference type, a
    /// nullable value type (<c>Nullable&lt;T&gt;</c>), or, defensively, since <c>default</c>
    /// means null for those two cases just as much as it means zero for a plain value type, a
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

    /// <summary>
    /// True when <paramref name="type"/> (unwrapping <c>Nullable&lt;T&gt;</c> first) formats
    /// differently depending on the current thread's culture: every numeric type, plus
    /// DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly. Deliberately excludes string, bool,
    /// char, Guid, and enum, none of those are meaningfully culture-sensitive (Guid's format is
    /// fixed regardless of culture; the others either have no culture-aware ToString overload at
    /// all, or their output doesn't vary by culture). The client emitter uses this to decide
    /// whether a value's ToString() call needs an explicit CultureInfo.InvariantCulture: without
    /// it, a value like 3.14m or a DateTime formats using the calling machine's OS locale, which
    /// may not match what the server's (invariant-culture-by-default) model binder expects --
    /// silently binding the wrong value, or failing to bind at all, depending entirely on the
    /// client's locale rather than anything under the API's control.
    /// </summary>
    private static bool IsCultureSensitiveType(ITypeSymbol type)
    {
        var underlying = type;

        if (underlying is INamedTypeSymbol { Name: "Nullable", IsGenericType: true } nullable)
            underlying = nullable.TypeArguments[0];

        if (underlying.SpecialType is
            SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Single or SpecialType.System_Double or
            SpecialType.System_Decimal)
        {
            return true;
        }

        return underlying.ToDisplayString(FullyQualified) is
            "global::System.DateTime" or "global::System.DateTimeOffset" or
            "global::System.TimeSpan" or "global::System.DateOnly" or "global::System.TimeOnly";
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

    /// <summary>Reports <see cref="RouteGenDiagnostics.UnsupportedSimpleType"/> unless <see cref="IsSimpleType"/> is true for <paramref name="type"/>. Used for route/query parameters only, <c>[Form]</c> has no such restriction, see <see cref="ApiParameterModel.IsSimpleType"/>.</summary>
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

    /// <summary>
    /// True when <paramref name="attributeType"/> is the RouteGen attribute named
    /// <paramref name="simpleName"/> specifically, i.e. it also lives in
    /// FlanderDev.RouteGen.Abstractions, not just any type from any assembly that happens to
    /// share the same simple name. Shared with <see cref="ApiContractGenerator"/> (via internal
    /// accessibility) rather than duplicated, so both places can't silently drift apart.
    /// </summary>
    internal static bool IsAttribute(INamedTypeSymbol? attributeType, string simpleName) =>
        attributeType is not null &&
        attributeType.Name == simpleName &&
        attributeType.ContainingNamespace?.ToDisplayString() == "FlanderDev.RouteGen.Abstractions";

    /// <summary>
    /// Reports <see cref="RouteGenDiagnostics.RouteCollision"/> for any two same-verb methods on
    /// <paramref name="model"/> whose routes are guaranteed to resolve identically at runtime --
    /// same literal text, same parameter positions, and the same constraint at each of those
    /// positions. Parameter NAMES are deliberately ignored for this comparison: ASP.NET Core's
    /// routing does not use them to disambiguate at all, so "{id:int}" and "{value:int}" at the
    /// same position are just as much a guaranteed collision as if they were named identically --
    /// comparing raw route strings (as this used to) would miss that.
    /// Also reports <see cref="RouteGenDiagnostics.OverlappingRoute"/> (a Warning, not an Error)
    /// for the narrower, non-guaranteed case: same shape, but the only difference is that one
    /// route leaves a position unconstrained while the sibling constrains it there. ASP.NET
    /// Core's constraint precedence CAN correctly disambiguate many such pairs at runtime, so this
    /// is flagged as worth double-checking rather than asserted as broken.
    /// Deliberately does not attempt to reason about optional route parameters creating
    /// variable-length effective routes (e.g. "{id:int?}" also matching a shorter sibling route)
    ///, that requires modeling ASP.NET Core's actual precedence rules, not just comparing
    /// parsed shapes, and is out of scope for this pass.
    /// </summary>
    private static void DetectRouteCollisions(
         ApiInterfaceModel model,
         List<Diagnostic> diagnostics)
    {
        var byVerb = model.Methods
            .GroupBy(m => m.Verb, System.StringComparer.OrdinalIgnoreCase);

        foreach (var verbGroup in byVerb)
        {
            var methods = verbGroup.ToList();

            for (int i = 0; i < methods.Count; i++)
            {
                for (int j = i + 1; j < methods.Count; j++)
                {
                    CompareRoutesForCollisionOrOverlap(model, methods[i], methods[j], diagnostics);
                }
            }
        }
    }

    /// <summary>Compares two same-verb methods' route shapes and reports RG0001 or RG0013 as appropriate; reports nothing if the routes simply don't overlap. See <see cref="DetectRouteCollisions"/> for the full rationale.</summary>
    private static void CompareRoutesForCollisionOrOverlap(
        ApiInterfaceModel model,
        ApiMethodModel a,
        ApiMethodModel b,
        List<Diagnostic> diagnostics)
    {
        var partsA = a.RouteTemplate.Parts;
        var partsB = b.RouteTemplate.Parts;

        if (partsA.Count != partsB.Count) return;

        bool shapeMatches = true;
        bool anyConstraintDiffers = false;

        for (int k = 0; k < partsA.Count; k++)
        {
            bool isParamA = partsA[k] is RouteParameterPart;
            bool isParamB = partsB[k] is RouteParameterPart;

            if (isParamA != isParamB)
            {
                shapeMatches = false;
                break;
            }

            if (!isParamA)
        {
                var literalA = (RouteLiteralPart)partsA[k];
                var literalB = (RouteLiteralPart)partsB[k];

                if (!string.Equals(literalA.Text, literalB.Text, System.StringComparison.OrdinalIgnoreCase))
                {
                    shapeMatches = false;
                    break;
                }
            }
            else
            {
                var paramA = (RouteParameterPart)partsA[k];
                var paramB = (RouteParameterPart)partsB[k];

                if (!string.Equals(paramA.Constraint, paramB.Constraint, System.StringComparison.OrdinalIgnoreCase))
                    anyConstraintDiffers = true;
            }
        }

        if (!shapeMatches) return;

        string routeA = a.RouteTemplate.Original;
        string routeB = b.RouteTemplate.Original;

        if (!anyConstraintDiffers)
            {
                diagnostics.Add(Diagnostic.Create(
                    RouteGenDiagnostics.RouteCollision,
                    Location.None,
                a.Name,
                b.Name,
                    model.InterfaceName,
                a.Verb,
                routeA));
            }
            else
            {
            diagnostics.Add(Diagnostic.Create(
                RouteGenDiagnostics.OverlappingRoute,
                Location.None,
                a.Name,
                b.Name,
                model.InterfaceName,
                a.Verb,
                routeA,
                routeB));
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
