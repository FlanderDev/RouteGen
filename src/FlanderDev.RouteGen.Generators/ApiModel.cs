using System.Collections.Generic;
using System.Linq;

namespace FlanderDev.RouteGen.Generators;

/// <summary>Parsed representation of a single <c>[ApiRoute]</c>-decorated interface.</summary>
internal sealed class ApiInterfaceModel(string @namespace, string interfaceName, string baseRoute)
{
    public string Namespace { get; } = @namespace;
    public string InterfaceName { get; } = interfaceName;
    public string BaseRoute { get; } = baseRoute;
    public string AbstractionsNamespace { get; set; } = "FlanderDev.RouteGen.Abstractions";
    public string HttpClientName { get; set; } = "Default";
    public bool InterfaceLevelAuthorize { get; set; }
    public string? InterfaceLevelRoles { get; set; }
    public string? InterfaceLevelPolicy { get; set; }
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

internal sealed class ApiMethodModel(string name, string verb, string? routeSuffix, RouteTemplate routeTemplate)
{
    public string Name { get; } = name;
    public string Verb { get; } = verb;
    public string? RouteSuffix { get; } = routeSuffix;
    public RouteTemplate RouteTemplate { get; } = routeTemplate;

    /// <summary>Full name of the Task&lt;T&gt; type argument, or null when the return type is bare Task.</summary>
    public string? ResponseTypeFullName { get; set; }

    public bool IsResponseNullable { get; set; }

    public bool IsStreamResponse { get; set; }
    public bool HasAuthorize { get; set; }
    public string? Roles { get; set; }
    public string? Policy { get; set; }
    public bool AllowAnonymous { get; set; }
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

internal enum ParameterKind
{
    RouteOrAuto,
    Query,
    Body,
    Form,
    File,
    CancellationToken
}

internal sealed class ApiParameterModel(string name, string typeFullName)
{
    public string Name { get; } = name;
    public string TypeFullName { get; } = typeFullName;

    public bool IsNullable { get; set; }

    public bool HasDefaultValue { get; set; }
    public string? DefaultValueLiteral { get; set; }
    public ParameterKind Kind { get; set; }
    public string? RouteTokenNameOverride { get; set; }
    public bool MatchesRouteToken { get; set; }
    public string? RouteConstraint { get; set; }

    /// <summary>
    /// True when a <see cref="ParameterKind.File"/> parameter is the multi-file form
    /// (<c>IReadOnlyList&lt;FormFile&gt;</c>, optionally nullable) rather than a single
    /// <c>FormFile</c>. Unused for every other <see cref="ParameterKind"/>.
    /// </summary>
    public bool IsMultiFile { get; set; }
}
