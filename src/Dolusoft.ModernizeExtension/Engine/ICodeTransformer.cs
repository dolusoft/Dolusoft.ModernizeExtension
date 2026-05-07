using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Dolusoft.ModernizeExtension.Engine;

internal interface ICodeTransformer
{
    string Name { get; }
    Task<Solution> TransformAsync(Solution solution, IReadOnlyList<DocumentId> scope, CancellationToken ct);
}
