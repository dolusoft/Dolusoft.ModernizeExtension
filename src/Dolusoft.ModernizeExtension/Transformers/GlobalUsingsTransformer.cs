using Dolusoft.ModernizeExtension.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

            // Use ALL project documents for frequency counting, not just those in scope.
            // This ensures accurate threshold calculation regardless of scope size.
            var allDocIds = project.Documents.Select(d => d.Id).ToList();

            // Preserved: regular namespace global usings already in GlobalUsings.cs (idempotency).
            // PreservedVerbatim: static/alias global usings kept verbatim to avoid losing keywords.
            // Frequency: how many source files declare each using (no static, no alias).
            // A using is worth globalizing only when it appears in max(3, ceil(N*0.5)) files,
            // where N is the total non-GlobalUsings source file count in the project.
            var preserved         = new SortedSet<string>();
            var preservedVerbatim = new SortedSet<string>(StringComparer.Ordinal);
            var frequency         = new Dictionary<string, int>(StringComparer.Ordinal);
            int sourceFileCount   = 0;

            foreach (var docId in allDocIds)
            {
                var doc = solution.GetDocument(docId);
                if (doc == null) continue;

                if (Path.GetFileName(doc.FilePath) == "GlobalUsings.cs")
                {
                    var existingRoot = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    if (existingRoot != null)
                    {
                        foreach (var gu in existingRoot.DescendantNodes().OfType<UsingDirectiveSyntax>()
                            .Where(u => !u.GlobalKeyword.IsKind(SyntaxKind.None)))
                        {
                            if (!gu.StaticKeyword.IsKind(SyntaxKind.None) || gu.Alias != null)
                                preservedVerbatim.Add(gu.ToString().Trim());
                            else
                                preserved.Add(gu.Name!.ToString());
                        }
                    }
                    continue;
                }

                var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root == null) continue;

                var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);

                sourceFileCount++;

                var topLevelUsings = root.ChildNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None)
                             && u.StaticKeyword.IsKind(SyntaxKind.None)
                             && u.Alias == null);

                foreach (var u in topLevelUsings)
                {
                    var name = u.Name!.ToString();

                    // Skip if the name resolves to a type rather than a namespace (CS0138 guard).
                    if (semanticModel != null)
                    {
                        var symbol = semanticModel.GetSymbolInfo(u.Name!).Symbol;
                        if (symbol != null && symbol is not INamespaceSymbol)
                            continue;
                    }

                    frequency.TryGetValue(name, out var count);
                    frequency[name] = count + 1;
                }
            }

            int threshold = Math.Max(3, (int)Math.Ceiling(sourceFileCount * 0.5));

            // Usings that newly qualify based on frequency
            var frequencyUsings = new SortedSet<string>(
                frequency.Where(kvp => kvp.Value >= threshold).Select(kvp => kvp.Key));

            var projectDir       = Path.GetDirectoryName(project.FilePath) ?? string.Empty;
            var globalUsingsPath = Path.Combine(projectDir, "GlobalUsings.cs");

            var existingDoc = project.Documents
                .FirstOrDefault(d => string.Equals(d.FilePath, globalUsingsPath, StringComparison.OrdinalIgnoreCase));

            // Issue 1: never create a new GlobalUsings.cs when nothing qualifies.
            // An existing file is always updated (to add new entries or stay in sync).
            if (existingDoc == null && frequencyUsings.Count == 0 && preservedVerbatim.Count == 0)
                continue;

            // Merge preserved + frequency entries
            var allUsings = new SortedSet<string>(preserved);
            allUsings.UnionWith(frequencyUsings);

            if (allUsings.Count == 0 && preservedVerbatim.Count == 0) continue;

            // Build GlobalUsings.cs content.
            // Verbatim entries (static/alias) come first, then regular namespace usings.
            var sb = new StringBuilder();
            foreach (var line in preservedVerbatim)
                sb.AppendLine(line);
            foreach (var ns in allUsings)
                sb.AppendLine($"global using {ns};");

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
                File.WriteAllText(globalUsingsPath, content, Encoding.UTF8);
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(project.Id, "GlobalUsings.cs"),
                    "GlobalUsings.cs",
                    sourceText,
                    filePath: globalUsingsPath);
            }

            // Remove collected usings from all project files
            solution = await RemoveUsingsFromDocumentsAsync(solution, allDocIds, allUsings, ct);
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
