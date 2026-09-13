using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FlanderDev.RouteGen.Migration;

/// <summary>
/// Generates a draft RouteGen interface from an attribute-routed controller (RGM0001). Supports
/// Fix-All (document/project/solution) via <see cref="WellKnownFixAllProviders.BatchFixer"/>,
/// which matters in practice: a real migration usually means dozens of controllers, not one.
/// Only ever adds a new file -- see the class remarks on <see cref="ControllerMigrationModel"/>
/// and the project README for why this deliberately never touches the controller itself.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ControllerMigrationCodeFixProvider))]
[Shared]
public sealed class ControllerMigrationCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        [MigrationDiagnostics.ControllerCanBeMigrated.Id];

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);

        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel is null) return;

        if (semanticModel.GetDeclaredSymbol(node, context.CancellationToken) is not INamedTypeSymbol controllerType)
            return;

        var model = ControllerMigrationReader.TryBuildModel(controllerType);
        if (model is null || model.Actions.Count == 0) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: $"Generate {model.InterfaceName} from {model.ControllerName}",
                createChangedSolution: ct => GenerateInterfaceAsync(context.Document.Project, model, ct),
                equivalenceKey: nameof(ControllerMigrationCodeFixProvider)),
            diagnostic);
    }

    private static Task<Solution> GenerateInterfaceAsync(
        Project controllerProject, ControllerMigrationModel model, CancellationToken ct)
    {
        string source = MigratedInterfaceEmitter.Emit(model);
        string fileName = model.InterfaceName + ".cs";

        var targetProject = FindSharedProject(controllerProject) ?? controllerProject;

        var newDocumentId = DocumentId.CreateNewId(targetProject.Id);
        var newSolution = targetProject.Solution.AddDocument(newDocumentId, fileName, SourceText.From(source));

        return Task.FromResult(newSolution);
    }

    /// <summary>
    /// Finds the one other project in the solution whose name, once the segments it shares with
    /// <paramref name="currentProject"/>'s dotted name are set aside, consists of exactly one
    /// remaining segment equal to "Shared" -- e.g. "SampleApp.Server" -> "SampleApp.Shared", or
    /// plain "Server" -> plain "Shared" in a solution with un-prefixed project names. Returns
    /// null if zero or more than one project matches, rather than guessing among candidates.
    /// </summary>
    private static Project? FindSharedProject(Project currentProject)
    {
        var currentSegments = currentProject.Name.Split('.');
        Project? match = null;
        int matchCount = 0;

        foreach (var candidate in currentProject.Solution.Projects)
        {
            if (candidate.Id == currentProject.Id) continue;

            var candidateSegments = candidate.Name.Split('.');

            int common = 0;
            while (common < currentSegments.Length && common < candidateSegments.Length &&
                   string.Equals(currentSegments[common], candidateSegments[common], System.StringComparison.OrdinalIgnoreCase))
            {
                common++;
            }

            bool remainderIsShared = candidateSegments.Length == common + 1 &&
                string.Equals(candidateSegments[common], "Shared", System.StringComparison.OrdinalIgnoreCase);

            if (remainderIsShared)
            {
                matchCount++;
                match = candidate;
            }
        }

        return matchCount == 1 ? match : null;
    }
}
