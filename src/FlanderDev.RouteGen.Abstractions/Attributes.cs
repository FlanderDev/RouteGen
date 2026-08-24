namespace FlanderDev.RouteGen.Abstractions;

/// <summary>
/// Marks an interface as the shared contract for an API surface. Applied once per interface
/// on the base route segment (e.g. "api/mods"). The RouteGen generators read this interface
/// via the semantic model in both the server and client compilations.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class ApiRouteAttribute(string template) : Attribute
{
    /// <summary>The base route template, e.g. "api/mods".</summary>
    public string Template { get; } = template;

    /// <summary>
    /// Name of the named <c>HttpClient</c> (registered via <c>IHttpClientFactory</c>) that the
    /// generated client implementation should resolve. Defaults to "Default" when not set.
    /// </summary>
    public string HttpClientName { get; set; } = "Default";
}

/// <summary>Base type for the per-method HTTP-verb attributes. Not intended to be used directly.</summary>
/// <param name="template"></param>
public abstract class HttpMethodAttribute(string? template) : Attribute
{
    /// <summary>Route template suffix appended to the interface-level <see cref="ApiRouteAttribute"/> template. May be null/empty.</summary>
    public string? Template { get; } = template;

    /// <summary>The HTTP verb this attribute represents (e.g. "GET").</summary>
    public abstract string Verb { get; }
}

#region HTTP Verb Attributes
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class GetAttribute(string? template = null) : HttpMethodAttribute(template)
{
    public override string Verb => "GET";
}

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PostAttribute(string? template = null) : HttpMethodAttribute(template)
{
    public override string Verb => "POST";
}

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PutAttribute(string? template = null) : HttpMethodAttribute(template)
{
    public override string Verb => "PUT";
}

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class DeleteAttribute(string? template = null) : HttpMethodAttribute(template)
{
    public override string Verb => "DELETE";
}

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PatchAttribute(string? template = null) : HttpMethodAttribute(template)
{
    public override string Verb => "PATCH";
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
#endregion

/// <summary>Marks a parameter as bound from the query string. Optional/nullable parameters are omitted from the generated client's query string when null/default.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class QueryAttribute : Attribute { }

/// <summary>Marks the (at most one) parameter serialized as the JSON request body.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class BodyAttribute : Attribute { }

/// <summary>
/// Marks a scalar/simple-type parameter as one field of a <c>multipart/form-data</c> request.
/// Combine with one or more <see cref="FileAttribute"/> parameters on the same method; a method
/// may use <see cref="Form"/>/<see cref="FileAttribute"/> or <see cref="BodyAttribute"/>, never
/// both -- a real HTTP request only has one content type, and RouteGen enforces that at compile
/// time (see diagnostic RG0009).
/// Server-side this becomes <c>[FromForm]</c>; client-side it's added to the generated
/// <c>MultipartFormDataContent</c> as a string part.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class FormAttribute : Attribute { }

/// <summary>
/// Marks a parameter as one (or, for a <c>IReadOnlyList&lt;FormFile&gt;</c>-typed parameter,
/// several) uploaded file(s) in a <c>multipart/form-data</c> request. The parameter type must be
/// <see cref="FormFile"/> for a single file, or <c>IReadOnlyList&lt;FormFile&gt;</c> (optionally
/// nullable) for multiple files under the same field name -- see diagnostic RG0010 for any other
/// type. Combine with <see cref="FormAttribute"/> parameters for accompanying form fields; not
/// combinable with <see cref="BodyAttribute"/> on the same method (RG0009).
/// Server-side a single file becomes <c>[FromForm] IFormFile</c>, and multiple files become
/// <c>[FromForm] List&lt;IFormFile&gt;</c>. Client-side, the generated implementation adds each
/// file's <see cref="FormFile.Content"/> stream to the request as a file part under the same
/// field name, using <see cref="FormFile.FileName"/> and <see cref="FormFile.ContentType"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class FileAttribute : Attribute { }

/// <summary>
/// Explicit override escape hatch: binds a parameter to a specific route-template token name
/// when it differs from the parameter's own name (route parameters are inferred by name-matching by default).
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class RouteAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

/// <summary>
/// Interface-level or method-level attribute controlling generated authorization requirements.
/// Mirrors ASP.NET Core's <c>AuthorizeAttribute</c> shape closely enough for the generator to
/// re-emit it onto the generated abstract controller base's action methods.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
public sealed class AuthorizeAttribute : Attribute
{
    public string? Roles { get; set; }
    public string? Policy { get; set; }
}

/// <summary>Marks a method as explicitly anonymous-accessible, overriding any interface-level <see cref="AuthorizeAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AllowAnonymousAttribute : Attribute { }

/// <summary>
/// Opt-in override for the page-route generator's default member-naming heuristic. Apply to a
/// Razor component with <c>@attribute [GeneratedPathName("ModDetail")]</c> when the default
/// derived name would be ambiguous or undesirable.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class GeneratedPathNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
