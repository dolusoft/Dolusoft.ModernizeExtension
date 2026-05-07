using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dolusoft.ModernizeExtension.Engine;

internal interface IProjectTransformer
{
    string Name { get; }
    Task TransformAsync(string solutionDirectory, IReadOnlyList<string> projectPaths, CancellationToken ct);
}
