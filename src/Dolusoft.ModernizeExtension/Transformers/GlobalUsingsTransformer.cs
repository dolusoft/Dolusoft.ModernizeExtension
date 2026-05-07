using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Dolusoft.ModernizeExtension.Transformers;

internal sealed class GlobalUsingsTransformer : ICodeTransformer
{
    public string Name => "Global Usings";

    public async Task<Solution> TransformAsync(Solution solution, IReadOnlyList<DocumentId> scope, CancellationToken ct)
    {
        // Group documents by project so we create one GlobalUsings.cs per project
        var byProject = scope
            .Select(id => solution.GetDocument(id))
            .Where(d => d != null)
            .GroupBy(d => d!.Project.Id)
            .ToList();

        foreach (var group in byProject)
        {
            ct.ThrowIfCancellationRequested();
            var project = solution.GetProject(group.Key);
            if (project == null) continue;

            // Collect all top-level, plain using directives (no static, no alias)
            var allUsings = new SortedSet<string>();
            var docIds    = group.Select(d => d!.Id).ToList();

            foreach (var docId in docIds)
            {
                var doc = solution.GetDocument(docId);
                if (doc == null) continue;

                // For an existing GlobalUsings.cs: preserve its entries so a second run
                // doesn't overwrite the file with a smaller set and lose already-moved usings.
                if (Path.GetFileName(doc.FilePath) == "GlobalUsings.cs")
                {
                    var existingRoot = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    if (existingRoot != null)
                    {
                        foreach (var gu in existingRoot.DescendantNodes().OfType<UsingDirectiveSyntax>()
                            .Where(u => !u.GlobalKeyword.IsKind(SyntaxKind.None)))
                            allUsings.Add(gu.Name!.ToString());
                    }
                    continue;
                }

                var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root == null) continue;

                // Only collect top-level usings (not inside a namespace block)
                var topLevelUsings = root.ChildNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)
                             && u.StaticKeyword.IsKind(SyntaxKind.None)
                             && u.Alias == null);

                foreach (var u in topLevelUsings)
                    allUsings.Add(u.Name!.ToString());
            }

            if (allUsings.Count == 0) continue;

            // Build GlobalUsings.cs content
            var sb = new StringBuilder();
            foreach (var ns in allUsings)
                sb.AppendLine($"global using {ns};");

            var projectDir    = Path.GetDirectoryName(project.FilePath) ?? string.Empty;
            var globalUsingsPath = Path.Combine(projectDir, "GlobalUsings.cs");

            // Add or replace GlobalUsings.cs in the solution
            var existingDoc = project.Documents
                .FirstOrDefault(d => string.Equals(d.FilePath, globalUsingsPath, System.StringComparison.OrdinalIgnoreCase));

            var content    = sb.ToString();
            var sourceText = SourceText.From(content, Encoding.UTF8);

            if (existingDoc != null)
            {
                solution = solution.WithDocumentText(existingDoc.Id, sourceText);
            }
            else
            {
                // Write directly to disk so VS's SDK-style project system (glob-based)
                // detects the file via file watcher before the user's next build.
                // TryApplyChanges alone may not notify the project system in time.
                File.WriteAllText(globalUsingsPath, content, Encoding.UTF8);
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(project.Id, "GlobalUsings.cs"),
                    "GlobalUsings.cs",
                    sourceText,
                    filePath: globalUsingsPath);
            }

            // Remove collected usings from individual files
            solution = await RemoveUsingsFromDocumentsAsync(solution, docIds, allUsings, ct);
        }

        return solution;
    }

    private static async Task<Solution> RemoveUsingsFromDocumentsAsync(
        Solution solution,
        IReadOnlyList<DocumentId> docIds,
        ISet<string> usingsToRemove,
        CancellationToken ct)
    {
        foreach (var docId in docIds)
        {
            ct.ThrowIfCancellationRequested();
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;
            if (Path.GetFileName(doc.FilePath) == "GlobalUsings.cs") continue;

            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) continue;

            var toRemove = root.ChildNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)
                         && u.StaticKeyword.IsKind(SyntaxKind.None)
                         && u.Alias == null
                         && usingsToRemove.Contains(u.Name!.ToString()))
                .ToList();

            if (toRemove.Count == 0) continue;

            var newRoot = root.RemoveNodes(toRemove, SyntaxRemoveOptions.KeepLeadingTrivia);
            if (newRoot != null)
            {
                // Strip any blank lines left at the top of the file.
                // KeepLeadingTrivia moves the trailing \r\n of removed using nodes
                // onto the next token, producing empty lines at the file start.
                var firstToken = newRoot.GetFirstToken();
                if (firstToken != default(SyntaxToken))
                {
                    var leading = firstToken.LeadingTrivia;
                    int trim = 0;
                    for (int i = 0; i < leading.Count; i++)
                    {
                        var k = leading[i].Kind();
                        if (k == SyntaxKind.EndOfLineTrivia || k == SyntaxKind.WhitespaceTrivia)
                            trim++;
                        else
                            break;
                    }
                    if (trim > 0)
                        newRoot = newRoot.ReplaceToken(
                            firstToken,
                            firstToken.WithLeadingTrivia(SyntaxFactory.TriviaList(leading.Skip(trim))));
                }
                solution = solution.WithDocumentSyntaxRoot(docId, newRoot);
            }
        }
        return solution;
    }
}
