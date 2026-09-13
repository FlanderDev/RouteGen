using Microsoft.CodeAnalysis;

namespace FlanderDev.RouteGen.Migration;

/// <summary>
/// The RouteGen.Migration (RGM00xx) diagnostic descriptors. Kept in a separate numbering space
/// from the main package's RG00xx diagnostics deliberately: those mean "something's wrong with
/// your RouteGen usage, fix it," reported as Error/Warning; this one means "here's a one-time
/// suggestion," reported as Info and meant to be acted on once, then forgotten -- mixing the two
/// categories into one sequence would make the Error List harder to reason about long after
/// migration is done and this package has been removed.
/// </summary>
internal static class MigrationDiagnostics
{
    /// <summary>The shared diagnostic category for every RouteGen.Migration diagnostic.</summary>
    private const string Category = "RouteGen.Migration";

    /// <summary>RGM0001: an attribute-routed controller looks migratable to a RouteGen interface.</summary>
    public static readonly DiagnosticDescriptor ControllerCanBeMigrated = new(
        id: "RGM0001",
        title: "Controller can be migrated to a RouteGen interface",
        messageFormat: "Controller '{0}' uses attribute routing and can be migrated to a RouteGen [ApiRoute] interface",
        category: Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Generates a starting-point RouteGen interface from this controller's existing [Route]/[Http*] attributes. This only generates the interface (a normal, editable file, not build-time generated output) -- it never modifies the controller itself. Constructs RouteGen doesn't model (e.g. [FromHeader], non-JSON results) are skipped per-action with a TODO comment rather than guessed at. Convention-routed controllers (no attribute routing at all) never trigger this, and once an interface named I{Stem}Api already exists anywhere in the solution, this stops firing for the matching controller.");
}
