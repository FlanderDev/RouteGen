using System.Runtime.CompilerServices;

// CodeFixes references this project's Compilation output normally, but needs to see the shared
// internal reader/model/emitter types (ControllerMigrationReader, ControllerMigrationModel,
// MigratedInterfaceEmitter, MigrationDiagnostics) -- kept internal rather than public since
// they're implementation details neither project's own consumers should depend on.
[assembly: InternalsVisibleTo("FlanderDev.RouteGen.Migration.CodeFixes")]
