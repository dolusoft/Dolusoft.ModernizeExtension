using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Infrastructure;
using Dolusoft.ModernizeExtension.Transformers;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;

namespace Dolusoft.ModernizeExtension.Engine;

internal sealed class ModernizationEngine
{
    private readonly VisualStudioWorkspace _workspace;
    private readonly ProgressReporter _progress;

    private readonly IReadOnlyList<IProjectTransformer> _projectTransformers;
    private readonly IReadOnlyList<ICodeTransformer> _codeTransformers;

    public ModernizationEngine(VisualStudioWorkspace workspace, ProgressReporter progress)
    {
        _workspace = workspace;
        _progress  = progress;

        _projectTransformers = new IProjectTransformer[]
        {
            new DirectoryBuildPropsTransformer()
        };

        _codeTransformers = new ICodeTransformer[]
        {
            new GlobalUsingsTransformer(),
            new FileScopedNamespaceTransformer(),
            new PrimaryConstructorTransformer(),
            new CollectionInitializerTransformer(),
            new CollectionExpressionTransformer(),
            new SimplifyNewExpressionTransformer()
        };
    }

    public async Task RunAsync(Solution solution, IReadOnlyList<DocumentId> scope, ModernizationScope modernizationScope, CancellationToken ct)
    {
        var totalSteps = (uint)(_projectTransformers.Count + _codeTransformers.Count);
        uint step = 0;

        // 1. Project-level (XML) transforms — only at Solution scope
        var solutionDir   = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;
        var projectPaths  = scope
            .Select(id => solution.GetDocument(id)?.Project.FilePath)
            .Where(p => p != null)
            .Distinct()
            .Select(p => p!)
            .ToList();

        foreach (var pt in _projectTransformers)
        {
            ct.ThrowIfCancellationRequested();
            _progress.Report($"{pt.Name}…", ++step, totalSteps);
            if (modernizationScope == ModernizationScope.Solution)
                await pt.TransformAsync(solutionDir, projectPaths, ct);
        }

        // 2. Code (Roslyn) transforms — each receives the accumulated solution
        var current = solution;
        foreach (var ct2 in _codeTransformers)
        {
            ct.ThrowIfCancellationRequested();
            _progress.Report($"{ct2.Name}…", ++step, totalSteps);
            if (ct2 is GlobalUsingsTransformer && modernizationScope == ModernizationScope.File)
                continue;
            current = await ct2.TransformAsync(current, scope, ct);
        }

        // 3. Commit all Roslyn changes in one operation — gives VS a single Undo entry
        if (current != solution)
            _workspace.TryApplyChanges(current);
    }
}
