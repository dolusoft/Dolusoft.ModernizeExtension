using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dolusoft.ModernizeExtension.Transformers;

/// <summary>
/// Replaces `new T(...)` with `new(...)` when the type is unambiguously inferrable
/// from an explicit variable/field declaration (IDE0090 equivalent).
/// Skips `var` declarations and cases where the declared type differs from the created type.
/// </summary>
internal sealed class SimplifyNewExpressionTransformer : ICodeTransformer
{
    public string Name => "Simplify New Expression";

    public async Task<Solution> TransformAsync(Solution solution, IReadOnlyList<DocumentId> scope, CancellationToken ct)
    {
        foreach (var docId in scope)
        {
            ct.ThrowIfCancellationRequested();
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;

            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) continue;

            var semanticModel = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (semanticModel == null) continue;

            var rewriter = new Rewriter(semanticModel);
            var newRoot  = rewriter.Visit(root);
            if (newRoot != root)
                solution = solution.WithDocumentSyntaxRoot(docId, newRoot);
        }
        return solution;
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;

        public Rewriter(SemanticModel model) => _model = model;

        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            // Recurse first so inner new-expressions are also simplified
            node = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;

            if (!CanSimplify(node)) return node;

            // new() — strip trailing space from 'new' (was between 'new' and the type name)
            var simplified = SyntaxFactory.ImplicitObjectCreationExpression(
                node.NewKeyword.WithTrailingTrivia(),
                node.ArgumentList ?? SyntaxFactory.ArgumentList(),
                node.Initializer
            ).WithTriviaFrom(node);

            return simplified;
        }

        private bool CanSimplify(ObjectCreationExpressionSyntax node)
        {
            // Only handle explicit local variable declarations with non-var type
            var parent = node.Parent;

            // `SomeType x = new SomeType(...)`  →  `SomeType x = new(...)`
            if (parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax decl } })
            {
                if (decl.Type.IsVar) return false;

                var declaredType = _model.GetTypeInfo(decl.Type).Type;
                var createdType  = _model.GetTypeInfo(node).Type;
                return declaredType != null
                    && createdType  != null
                    && SymbolEqualityComparer.Default.Equals(declaredType, createdType);
            }

            // `private readonly T _x = new T(...)` (field initializer)
            if (parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: FieldDeclarationSyntax field } })
            {
                var declaredType = _model.GetTypeInfo(field.Declaration.Type).Type;
                var createdType  = _model.GetTypeInfo(node).Type;
                return declaredType != null
                    && createdType  != null
                    && SymbolEqualityComparer.Default.Equals(declaredType, createdType);
            }

            return false;
        }
    }
}
