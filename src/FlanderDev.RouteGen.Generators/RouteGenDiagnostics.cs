using Microsoft.CodeAnalysis;

namespace FlanderDev.RouteGen.Generators;

/// <summary>The RouteGen (RG00xx) diagnostic descriptors, reported while parsing <c>[ApiRoute]</c> interfaces and <c>@page</c> directives.</summary>
internal static class RouteGenDiagnostics
{
    /// <summary>The shared diagnostic category for every RouteGen diagnostic.</summary>
    private const string Category = "RouteGen";

    /// <summary>RG0001: two methods on the same interface resolve to the same verb and route.</summary>
    public static readonly DiagnosticDescriptor RouteCollision = new(
        id: "RG0001",
        title: "Duplicate route + verb",
        messageFormat: "Methods '{0}' and '{1}' on interface '{2}' both resolve to '{3} {4}'",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Two methods on the same interface produce an identical resulting route and HTTP verb, which ASP.NET Core cannot disambiguate at runtime.");

    /// <summary>RG0002: a <c>[Body]</c> parameter is used on a GET/DELETE method (warning).</summary>
    public static readonly DiagnosticDescriptor BodyOnNonBodyVerb = new(
        id: "RG0002",
        title: "[Body] used with a verb that does not accept a body",
        messageFormat: "Parameter '{0}' on method '{1}' is marked [Body] but the method is [{2}], which conventionally has no request body",
        category: Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "[Body] combined with [Get] or [Delete] is a nonsensical combination for most APIs; flagged as a warning since some servers do accept it.");

    /// <summary>RG0003: a route template <c>{token}</c> has no matching parameter.</summary>
    public static readonly DiagnosticDescriptor UnmatchedRouteToken = new(
        id: "RG0003",
        title: "Route token has no matching parameter",
        messageFormat: "Route template '{0}' on method '{1}' contains token '{{{2}}}' with no matching parameter",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every {token} in a route template must correspond to a method parameter of the same name (or one annotated with [Route(\"token\")]).");

    /// <summary>RG0004: a parameter doesn't match a route token and isn't <c>[Query]</c>/<c>[Body]</c>/<c>[Form]</c>/<c>[File]</c>.</summary>
    public static readonly DiagnosticDescriptor UnmatchedParameter = new(
        id: "RG0004",
        title: "Parameter does not appear in route template and is not [Query] or [Body]",
        messageFormat: "Parameter '{0}' on method '{1}' does not match any route token and is not marked [Query] or [Body]",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A parameter must either match a {token} in the route template, or be explicitly marked [Query] or [Body] so the generator knows how to bind it.");

    /// <summary>RG0005: more than one parameter on a method is marked <c>[Body]</c>.</summary>
    public static readonly DiagnosticDescriptor MultipleBodyParameters = new(
        id: "RG0005",
        title: "More than one [Body] parameter",
        messageFormat: "Method '{0}' has more than one parameter marked [Body]; only one request body is supported",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "At most one parameter per method may be marked [Body].");

    /// <summary>RG0006: a route/query/form parameter's type isn't a simple, URL-representable type.</summary>
    public static readonly DiagnosticDescriptor UnsupportedSimpleType = new(
        id: "RG0006",
        title: "Type is not convertible to/from a URL segment or query string",
        messageFormat: "Parameter '{0}' on method '{1}' has type '{2}', which is not a primitive, string, enum, Guid, DateTime, or similar simple type expected for a route/query parameter",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Route and query parameters must be simple, URL-representable types. Use [Body] for complex object types.");

    /// <summary>RG0007: two <c>@page</c> directives would generate the same <c>Paths</c> member name.</summary>
    public static readonly DiagnosticDescriptor AmbiguousPageRouteMember = new(
        id: "RG0007",
        title: "Ambiguous generated Paths member name",
        messageFormat: "Two or more @page directives would generate the same Paths member name '{0}'; add [GeneratedPathName(\"...\")] to disambiguate",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The page-route generator derives member names from the component's file name by default; when two components collide, use [GeneratedPathName] to pick an explicit name.");

    /// <summary>RG0008: a route template could not be parsed.</summary>
    public static readonly DiagnosticDescriptor InvalidRouteTemplate = new(
        id: "RG0008",
        title: "Invalid or unparsable route template",
        messageFormat: "Could not parse route template '{0}' on '{1}': {2}",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The route template could not be parsed into literal segments and {token} placeholders.");

    /// <summary>RG0009: a method mixes a <c>[Body]</c> parameter with <c>[Form]</c>/<c>[File]</c> parameters.</summary>
    public static readonly DiagnosticDescriptor MixedBodyAndMultipart = new(
        id: "RG0009",
        title: "[Body] combined with [Form]/[File] on the same method",
        messageFormat: "Method '{0}' has both a [Body] parameter and a [Form]/[File] parameter; a request can only have one content type -- pick JSON ([Body]) or multipart/form-data ([Form]/[File]), not both",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A single HTTP request has exactly one Content-Type, so a method cannot mix a JSON [Body] parameter with [Form]/[File] multipart parameters.");

    /// <summary>RG0010: a <c>[File]</c> parameter's type isn't a shape ASP.NET Core's model binder is verified to support.</summary>
    public static readonly DiagnosticDescriptor InvalidFileParameterType = new(
        id: "RG0010",
        title: "[File] parameter has an unsupported type",
        messageFormat: "Parameter '{0}' on method '{1}' is marked [File] but has type '{2}'; [File] parameters must be FormFile/FormFile<TMetadata> (single file), an array of either, one of IEnumerable<>/ICollection<>/IList<>/IReadOnlyList<>/IReadOnlyCollection<>/List<> of either, or another concrete generic collection type with a public parameterless constructor implementing ICollection<> (optionally nullable)",
        category: Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The server mirrors the client's exact declared collection type (substituting IFormFile for FormFile), so only shapes ASP.NET Core's model binder is verified to construct without throwing at request time are accepted -- see TryGetFileParameterShape for the exact rule, checked against ModelBindingHelper.GetCompatibleCollection<T>.");
}
