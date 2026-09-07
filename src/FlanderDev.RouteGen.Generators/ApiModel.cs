using System.Collections.Generic;
using System.Linq;

namespace FlanderDev.RouteGen.Generators;

/// <summary>Parsed representation of a single <c>[ApiRoute]</c>-decorated interface.</summary>
/// <param name="namespace">The interface's containing namespace, or empty for the global namespace.</param>
/// <param name="interfaceName">The interface's simple name, e.g. "IModsApi".</param>
/// <param name="baseRoute">The base route template from <c>[ApiRoute]</c>, e.g. "api/mods".</param>
internal sealed class ApiInterfaceModel(string @namespace, string interfaceName, string baseRoute)
{
    /// <summary>The interface's containing namespace, or empty for the global namespace.</summary>
    public string Namespace { get; } = @namespace;

    /// <summary>The interface's simple name, e.g. "IModsApi".</summary>
    public string InterfaceName { get; } = interfaceName;

    /// <summary>The base route template from <c>[ApiRoute]</c>, e.g. "api/mods".</summary>
    public string BaseRoute { get; } = baseRoute;

    /// <summary>Namespace the RouteGen attribute types were resolved from, used to qualify references to them in generated code.</summary>
    public string AbstractionsNamespace { get; set; } = "FlanderDev.RouteGen.Abstractions";

    /// <summary>Name of the named <c>HttpClient</c> the generated client resolves via <c>IHttpClientFactory</c>.</summary>
    public string HttpClientName { get; set; } = "Default";

    /// <summary>True when the interface itself carries an <c>[Authorize]</c> attribute, inherited by methods that don't declare their own.</summary>
    public bool InterfaceLevelAuthorize { get; set; }

    /// <summary>Roles from the interface-level <c>[Authorize]</c>, if any.</summary>
    public string? InterfaceLevelRoles { get; set; }

    /// <summary>Policy from the interface-level <c>[Authorize]</c>, if any.</summary>
    public string? InterfaceLevelPolicy { get; set; }

    /// <summary>Every parsed HTTP-verb-attributed method on the interface.</summary>
    public List<ApiMethodModel> Methods { get; } = [];

    /// <summary>
    /// Generated type name stem, e.g. "Mods" from "IModsApi" (strips a leading "I" and a
    /// trailing "Api" when present).
    /// </summary>
    public string ShortName
    {
        get
        {
            string name = InterfaceName;
            if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]))
                name = name.Substring(1);
            if (name.Length > 3 && name.EndsWith("Api"))
                name = name.Substring(0, name.Length - 3);
            return name.Length == 0 ? InterfaceName : name;
        }
    }
}

/// <summary>Parsed representation of a single HTTP-verb-attributed interface method.</summary>
/// <param name="name">The method's simple name.</param>
/// <param name="verb">The HTTP verb, e.g. "GET".</param>
/// <param name="routeSuffix">The route template suffix from the verb attribute, if any.</param>
/// <param name="routeTemplate">The combined (base route + suffix) parsed route template.</param>
internal sealed class ApiMethodModel(string name, string verb, string? routeSuffix, RouteTemplate routeTemplate)
{
    /// <summary>The method's simple name.</summary>
    public string Name { get; } = name;

    /// <summary>The HTTP verb, e.g. "GET".</summary>
    public string Verb { get; } = verb;

    /// <summary>The route template suffix from the verb attribute, if any.</summary>
    public string? RouteSuffix { get; } = routeSuffix;

    /// <summary>The combined (base route + suffix) parsed route template.</summary>
    public RouteTemplate RouteTemplate { get; } = routeTemplate;

    /// <summary>Full name of the Task&lt;T&gt; type argument, or null when the return type is bare Task.</summary>
    public string? ResponseTypeFullName { get; set; }

    /// <summary>
    /// True when the response type argument itself is nullable (<c>Task&lt;ModDto?&gt;</c>).
    /// The client emitter uses this to decide whether it's safe to force-unwrap the result of
    /// <c>ReadFromJsonAsync</c> (which always returns a nullable T, regardless of whether the
    /// interface's declared T is itself annotated nullable) with the null-forgiving operator.
    /// </summary>
    public bool IsResponseNullable { get; set; }

    /// <summary>True when the response type argument is <c>Stream</c>, meaning the response is read as a raw stream rather than deserialized as JSON.</summary>
    public bool IsStreamResponse { get; set; }

    /// <summary>True when this method should carry a generated <c>[Authorize]</c> attribute.</summary>
    public bool HasAuthorize { get; set; }

    /// <summary>Roles for the generated <c>[Authorize]</c>, if any.</summary>
    public string? Roles { get; set; }

    /// <summary>Policy for the generated <c>[Authorize]</c>, if any.</summary>
    public string? Policy { get; set; }

    /// <summary>True when this method carries <c>[AllowAnonymous]</c>, overriding any inherited interface-level authorization.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Every parsed parameter on this method, including <see cref="ParameterKind.CancellationToken"/> parameters.</summary>
    public List<ApiParameterModel> Parameters { get; } = [];

    /// <summary>
    /// True when this method has one or more <see cref="ParameterKind.Form"/>/
    /// <see cref="ParameterKind.File"/> parameters, meaning both emitters must produce
    /// <c>multipart/form-data</c> binding/request-building instead of the JSON path used for
    /// <see cref="ParameterKind.Body"/>. Mutually exclusive with a <c>[Body]</c> parameter --
    /// enforced as diagnostic RG0009, since an HTTP request can only have one content type.
    /// </summary>
    public bool UsesMultipart => Parameters.Any(p => p.Kind is ParameterKind.Form or ParameterKind.File);
}

/// <summary>How a parameter is bound to the HTTP request, driving both the server- and client-side emitters.</summary>
internal enum ParameterKind
{
    /// <summary>Bound from a route segment matching the parameter's name (or <c>[Route]</c> override).</summary>
    RouteOrAuto,

    /// <summary>Bound from the query string via <c>[Query]</c>.</summary>
    Query,

    /// <summary>The JSON request body via <c>[Body]</c>.</summary>
    Body,

    /// <summary>One <c>multipart/form-data</c> field via <c>[Form]</c>.</summary>
    Form,

    /// <summary>One or more uploaded files via <c>[File]</c>.</summary>
    File,

    /// <summary>The method's <see cref="System.Threading.CancellationToken"/> parameter, never bound from the request itself.</summary>
    CancellationToken
}

/// <summary>Parsed representation of a single method parameter.</summary>
/// <param name="name">The parameter's name.</param>
/// <param name="typeFullName">The parameter's fully-qualified, nullable-annotation-aware type name.</param>
internal sealed class ApiParameterModel(string name, string typeFullName)
{
    /// <summary>The parameter's name.</summary>
    public string Name { get; } = name;

    /// <summary>The parameter's fully-qualified, nullable-annotation-aware type name.</summary>
    public string TypeFullName { get; } = typeFullName;

    /// <summary>
    /// True when this parameter can legitimately hold null at runtime: a nullable reference
    /// type (<c>string?</c>), a nullable value type (<c>int?</c> / <c>Nullable&lt;T&gt;</c>), or
    /// (defensively) any parameter whose declared default value is literally <c>null</c>.
    /// Both emitters use this instead of string-sniffing <see cref="TypeFullName"/> for "?".
    /// </summary>
    public bool IsNullable { get; set; }

    /// <summary>True when the interface method declared an explicit default value for this parameter.</summary>
    public bool HasDefaultValue { get; set; }

    /// <summary>The parameter's default value, formatted as a C# literal, when <see cref="HasDefaultValue"/> is true.</summary>
    public string? DefaultValueLiteral { get; set; }

    /// <summary>How this parameter is bound to the HTTP request.</summary>
    public ParameterKind Kind { get; set; }

    /// <summary>The <c>[Route("...")]</c> token-name override, if the parameter used one.</summary>
    public string? RouteTokenNameOverride { get; set; }

    /// <summary>True when this parameter matches a <c>{token}</c> in the route template.</summary>
    public bool MatchesRouteToken { get; set; }

    /// <summary>The matched route token's constraint text (e.g. "int"), if any.</summary>
    public string? RouteConstraint { get; set; }

    /// <summary>
    /// True when this parameter's type is a route/query-safe simple type (primitive, string,
    /// enum, Guid, DateTime, etc.), optionally nullable. Meaningful only for
    /// <see cref="ParameterKind.Form"/>: a simple-typed <c>[Form]</c> field is sent/bound as a
    /// plain string (unchanged behavior); anything else is JSON-serialized into the field
    /// instead, since <c>[Form]</c> places no type restriction the way <see cref="RouteGenDiagnostics.UnsupportedSimpleType"/>
    /// enforces for <see cref="ParameterKind.Query"/>/<see cref="ParameterKind.RouteOrAuto"/>.
    /// </summary>
    public bool IsSimpleType { get; set; }

    /// <summary>
    /// True when a <see cref="ParameterKind.File"/> parameter is the multi-file form
    /// (any accepted collection shape, optionally nullable) rather than a single
    /// <c>FormFile</c>. Unused for every other <see cref="ParameterKind"/>.
    /// </summary>
    public bool IsMultiFile { get; set; }

    /// <summary>
    /// For a <see cref="ParameterKind.File"/> parameter, the fully-qualified server-side type to
    /// generate: the same shape as <see cref="TypeFullName"/> (single value, array, or a
    /// verified-bindable collection type) with <c>IFormFile</c> substituted for
    /// <c>FormFile</c>/<c>FormFile&lt;TMetadata&gt;</c>. Null for every other
    /// <see cref="ParameterKind"/>.
    /// </summary>
    public string? ServerFileTypeFullName { get; set; }
}
