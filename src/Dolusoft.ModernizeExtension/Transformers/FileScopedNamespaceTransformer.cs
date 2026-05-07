using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Dolusoft.ModernizeExtension.Transformers;

internal sealed class FileScopedNamespaceTransformer : CSharpSyntaxRewriter, ICodeTransformer
{
    public string Name => "File-Scoped Namespace";

    public async Task<Solution> TransformAsync(Solution solution, IReadOnlyList<DocumentId> scope, CancellationToken ct)
    {
        foreach (var docId in scope)
        {
            ct.ThrowIfCancellationRequested();
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;

            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) continue;

            // Skip files that have no block-scoped namespace or already have file-scoped
            var namespaces = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().ToList();
            if (namespaces.Count == 0) continue;

            // Multiple namespaces per file cannot be converted — file-scoped allows only one
            if (namespaces.Count > 1) continue;

            var newRoot = Visit(root);
            if (newRoot == root) continue;

            // Run the Roslyn formatter to fix indentation after removing the namespace block
            var newDoc = solution.WithDocumentSyntaxRoot(docId, newRoot).GetDocument(docId)!;
            var formattedDoc = await Formatter.FormatAsync(newDoc, cancellationToken: ct).ConfigureAwait(false);
            solution = formattedDoc.Project.Solution;
        }
        return solution;
    }

    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        // node.Name carries trailing trivia (the \r\n before the opening {).
        // Strip it so the semicolon lands on the same line as the namespace name.
        var fileScoped = SyntaxFactory.FileScopedNamespaceDeclaration(
            attributeLists:   node.AttributeLists,
            modifiers:        node.Modifiers,
            namespaceKeyword: node.NamespaceKeyword.WithTrailingTrivia(SyntaxFactory.Space),
            name:             node.Name.WithTrailingTrivia(),
            semicolonToken:   SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                                  .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed),
            externs:          node.Externs,
            usings:           node.Usings,
            members:          node.Members
        );

        return fileScoped.WithLeadingTrivia(node.GetLeadingTrivia())
                         .WithTrailingTrivia(node.GetTrailingTrivia());
    }
}
