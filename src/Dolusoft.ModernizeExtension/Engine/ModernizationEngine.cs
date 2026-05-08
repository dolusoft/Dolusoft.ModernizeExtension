using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Infrastructure;
using Dolusoft.ModernizeExtension.Options;
using Dolusoft.ModernizeExtension.Transformers;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;

namespace Dolusoft.ModernizeExtension.Engine;

internal sealed class ModernizationEngine
{
    private readonly VisualStudioWorkspace _workspace;
    private readonly ProgressReporter _progress;
    private readonly ModernizeOptions _options;

    private readonly IReadOnlyList<IProjectTransformer> _projectTransformers;
    private readonly IReadOnlyList<ICodeTransformer> _codeTransformers;

    public ModernizationEngine(VisualStudioWorkspace workspace, ProgressReporter progress, ModernizeOptions options)
    {
        _workspace = workspace;
        _progress  = progress;
        _options   = options;

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

    public async Task<Solution> RunAsync(Solution solution, IReadOnlyList<DocumentId> scope, ModernizationScope modernizationScope, CancellationToken ct)
    {
        var totalSteps = (uint)(_projectTransformers.Count + _codeTransformers.Count);
        uint step = 0;

        // 1. Project-level (XML) transforms — only at Solution scope
        var solutionDir  = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;
        var projectPaths = scope
            .Select(id => solution.GetDocument(id)?.Project.FilePath)
            .Where(p => p != null)
            .Distinct()
            .Select(p => p!)
            .ToList();

        foreach (var pt in _projectTransformers)
        {
            ct.ThrowIfCancellationRequested();
            _progress.Report($"{pt.Name}…", ++step, totalSteps);
            if (modernizationScope == ModernizationScope.Solution && IsEnabled(pt))
                await pt.TransformAsync(solutionDir, projectPaths, ct);
        }

        // 2. Code (Roslyn) transforms — each receives the accumulated solution
        var current = solution;
        foreach (var ct2 in _codeTransformers)
        {
            ct.ThrowIfCancellationRequested();
            _progress.Report($"{ct2.Name}…", ++step, totalSteps);
            if (!IsEnabled(ct2)) continue;
            if (ct2 is GlobalUsingsTransformer && modernizationScope == ModernizationScope.File) continue;
            current = await ct2.TransformAsync(current, scope, ct);
        }

        // Return the accumulated solution — the caller is responsible for calling
        // TryApplyChanges on the main thread.
        return current;
    }

    private bool IsEnabled(IProjectTransformer transformer) => transformer switch
    {
        DirectoryBuildPropsTransformer => _options.DirectoryBuildProps,
        _ => true
    };

    private bool IsEnabled(ICodeTransformer transformer) => transformer switch
    {
        GlobalUsingsTransformer          => _options.GlobalUsings,
        FileScopedNamespaceTransformer   => _options.FileScopedNamespace,
        PrimaryConstructorTransformer    => _options.PrimaryConstructor,
        CollectionInitializerTransformer => _options.CollectionInitializers,
        CollectionExpressionTransformer  => _options.CollectionExpressions,
        SimplifyNewExpressionTransformer => _options.SimplifyNewExpression,
        _ => true
    };
}
