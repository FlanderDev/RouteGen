using System.Collections.Generic;

namespace FlanderDev.RouteGen.Migration;

/// <summary>Parsed representation of an attribute-routed controller being migrated to a RouteGen interface.</summary>
/// <param name="namespace">The controller's containing namespace.</param>
/// <param name="controllerName">The controller class's simple name, e.g. "ModsController".</param>
/// <param name="baseRoute">The resolved base route (with any "[controller]" token already substituted).</param>
public sealed class ControllerMigrationModel(string @namespace, string controllerName, string baseRoute)
{
    /// <summary>The controller's containing namespace.</summary>
    public string Namespace { get; } = @namespace;

    /// <summary>The controller class's simple name, e.g. "ModsController".</summary>
    public string ControllerName { get; } = controllerName;

    /// <summary>The resolved base route (with any "[controller]" token already substituted).</summary>
    public string BaseRoute { get; } = baseRoute;

    /// <summary>Every action that was successfully translated, in declaration order.</summary>
    public List<ActionMigrationModel> Actions { get; } = [];

    /// <summary>One line per hard-skipped action (Q4(a): omitted entirely, noted why) for the class-level TODO banner.</summary>
    public List<string> SkippedActionNotes { get; } = [];

    /// <summary>
    /// Simple names of types referenced by the interface that are declared in the same assembly
    /// as the controller -- i.e. likely private to the Server project today, and will need to
    /// move to Shared too before the generated interface can compile there.
    /// </summary>
    public List<string> LocalTypeNames { get; } = [];

    /// <summary>The interface name to generate: "I{Stem}Api", e.g. "IModsApi" for "ModsController".</summary>
    public string InterfaceName => "I" + ControllerMigrationReader.StemFromControllerName(ControllerName) + "Api";
}

/// <summary>One successfully-translated action method.</summary>
/// <param name="name">The action method's name.</param>
/// <param name="verb">The RouteGen verb attribute name to emit, e.g. "Get".</param>
/// <param name="routeSuffix">The route template suffix (with any "[action]" token already substituted), if any.</param>
public sealed class ActionMigrationModel(string name, string verb, string? routeSuffix)
{
    /// <summary>The action method's name.</summary>
    public string Name { get; } = name;

    /// <summary>The RouteGen verb attribute name to emit, e.g. "Get".</summary>
    public string Verb { get; } = verb;

    /// <summary>The route template suffix (with any "[action]" token already substituted), if any.</summary>
    public string? RouteSuffix { get; } = routeSuffix;

    /// <summary>The unwrapped response type's fully-qualified name, or null for a bare <c>Task</c>.</summary>
    public string? ResponseTypeFullName { get; set; }

    /// <summary>True when the unwrapped response type is <c>Stream</c>.</summary>
    public bool IsStreamResponse { get; set; }

    /// <summary>True when the action carries (or inherits) <c>[Authorize]</c>.</summary>
    public bool HasAuthorize { get; set; }

    /// <summary>Roles from <c>[Authorize(Roles = ...)]</c>, if any.</summary>
    public string? Roles { get; set; }

    /// <summary>Policy from <c>[Authorize(Policy = ...)]</c>, if any.</summary>
    public string? Policy { get; set; }

    /// <summary>True when the action carries <c>[AllowAnonymous]</c>.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Every successfully-mapped parameter, in declaration order.</summary>
    public List<ActionParameterMigrationModel> Parameters { get; } = [];

    /// <summary>
    /// One line per attribute that was recognized but isn't part of RouteGen's vocabulary (e.g.
    /// <c>[Produces]</c>, <c>[ServiceFilter]</c>) -- dropped from the generated interface, but
    /// noted with a TODO comment rather than silently discarded.
    /// </summary>
    public List<string> DroppedAttributeNotes { get; } = [];
}

/// <summary>How a migrated parameter should be bound in the generated RouteGen interface.</summary>
public enum MigratedParameterKind
{
    /// <summary>Becomes a route-matched parameter (no attribute needed; RouteGen infers it from the route template).</summary>
    Route,

    /// <summary><c>[Query]</c>.</summary>
    Query,

    /// <summary><c>[Body]</c>.</summary>
    Body,

    /// <summary><c>[Form]</c>.</summary>
    Form,

    /// <summary><c>[File]</c>.</summary>
    File,

    /// <summary>Carried over as-is; never bound from the request.</summary>
    CancellationToken
}

/// <summary>One successfully-mapped action parameter.</summary>
/// <param name="name">The parameter's name.</param>
/// <param name="typeFullName">
/// The parameter's type as it should appear in the generated interface -- for
/// <see cref="MigratedParameterKind.File"/> this is already "FormFile" or
/// "IReadOnlyList&lt;FormFile&gt;", not the original IFormFile-shaped ASP.NET Core type.
/// </param>
/// <param name="kind">How this parameter should be bound in the generated interface.</param>
public sealed class ActionParameterMigrationModel(string name, string typeFullName, MigratedParameterKind kind)
{
    /// <summary>The parameter's name.</summary>
    public string Name { get; } = name;

    /// <summary>The parameter's type as it should appear in the generated interface.</summary>
    public string TypeFullName { get; } = typeFullName;

    /// <summary>How this parameter should be bound in the generated interface.</summary>
    public MigratedParameterKind Kind { get; } = kind;

    /// <summary>True when a <see cref="MigratedParameterKind.File"/> parameter is the multi-file form.</summary>
    public bool IsMultiFile { get; set; }
}
