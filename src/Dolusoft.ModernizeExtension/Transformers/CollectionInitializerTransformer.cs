using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dolusoft.ModernizeExtension.Transformers;

internal sealed class CollectionInitializerTransformer : ICodeTransformer
{
    public string Name => "Collection Initializers";

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

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            node = (BlockSyntax)base.VisitBlock(node)!;

            var statements = node.Statements.ToList();
            var result     = new List<StatementSyntax>();
            bool changed   = false;
            int  i         = 0;

            while (i < statements.Count)
            {
                if (TryExtractCollectionDecl(statements[i],
                        out var varName, out var localDecl, out var creation))
                {
                    // Collect only consecutive variable.Add(...) calls
                    var addItems = new List<IReadOnlyList<ArgumentSyntax>>();
                    int j = i + 1;
                    while (j < statements.Count &&
                           TryGetAddCall(statements[j], varName!, out var args))
                    {
                        addItems.Add(args!);
                        j++;
                    }

                    if (addItems.Count > 0)
                    {
                        var initExprs = BuildInitExprs(addItems);
                        var seps = Enumerable.Repeat(
                            SyntaxFactory.Token(SyntaxKind.CommaToken)
                                .WithTrailingTrivia(SyntaxFactory.Space),
                            initExprs.Count - 1);

                        var initializer = SyntaxFactory.InitializerExpression(
                            SyntaxKind.CollectionInitializerExpression,
                            SyntaxFactory.SeparatedList<ExpressionSyntax>(initExprs, seps))
                            .WithLeadingTrivia(SyntaxFactory.Space);

                        // Build the new creation expression — handles both new T() and new()
                        ExpressionSyntax newCreation = creation switch
                        {
                            ObjectCreationExpressionSyntax   oce => oce.WithInitializer(initializer),
                            ImplicitObjectCreationExpressionSyntax ioe => ioe.WithInitializer(initializer),
                            _ => creation!
                        };

                        var oldDeclarator = localDecl!.Declaration.Variables[0];
                        var newDeclarator = oldDeclarator.WithInitializer(
                            oldDeclarator.Initializer!.WithValue(newCreation));
                        var newLocalDecl  = localDecl.WithDeclaration(
                            localDecl.Declaration.WithVariables(
                                SyntaxFactory.SingletonSeparatedList(newDeclarator)));

                        result.Add(newLocalDecl);
                        i       = j;
                        changed = true;
                        continue;
                    }
                }

                result.Add(statements[i++]);
            }

            return changed ? node.WithStatements(SyntaxFactory.List(result)) : node;
        }

        private bool TryExtractCollectionDecl(
            StatementSyntax stmt,
            out string? varName,
            out LocalDeclarationStatementSyntax? localDecl,
            out ExpressionSyntax? creation)
        {
            varName   = null;
            localDecl = null;
            creation  = null;

            if (stmt is not LocalDeclarationStatementSyntax lds) return false;
            if (lds.Declaration.Variables.Count != 1) return false;

            var declarator = lds.Declaration.Variables[0];
            var initValue  = declarator.Initializer?.Value;

            // Accept explicit new T()/new T(args) and target-typed new()/new(args).
            // Reject anything that already has a { } initializer.
            bool alreadyInitialized = initValue switch
            {
                ObjectCreationExpressionSyntax         oce => oce.Initializer != null,
                ImplicitObjectCreationExpressionSyntax ioe => ioe.Initializer != null,
                _ => true   // not a creation expression
            };

            if (alreadyInitialized) return false;

            // Verify the type exposes a public Add method (collection initializer requirement)
            var type = _model.GetTypeInfo(initValue!).Type;
            if (type == null) return false;
            if (!type.GetMembers("Add").OfType<IMethodSymbol>().Any()) return false;

            varName   = declarator.Identifier.Text;
            localDecl = lds;
            creation  = initValue;
            return true;
        }

        private static bool TryGetAddCall(
            StatementSyntax stmt,
            string varName,
            out IReadOnlyList<ArgumentSyntax>? args)
        {
            args = null;
            if (stmt is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax inv }) return false;
            if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } mae) return false;
            if (mae.Expression is not IdentifierNameSyntax id || id.Identifier.Text != varName) return false;
            if (inv.ArgumentList.Arguments.Count == 0) return false;

            args = inv.ArgumentList.Arguments.ToList();
            return true;
        }

        private static List<ExpressionSyntax> BuildInitExprs(IReadOnlyList<IReadOnlyList<ArgumentSyntax>> addItems)
        {
            var result = new List<ExpressionSyntax>();
            foreach (var args in addItems)
            {
                if (args.Count == 1)
                {
                    result.Add(args[0].Expression.WithoutLeadingTrivia());
                }
                else
                {
                    // Multi-arg Add (e.g. Dictionary<K,V>): wrap in { key, value }
                    var innerExprs = args.Select(a => a.Expression.WithoutLeadingTrivia()).ToList();
                    var innerSeps  = Enumerable.Repeat(
                        SyntaxFactory.Token(SyntaxKind.CommaToken)
                            .WithTrailingTrivia(SyntaxFactory.Space),
                        innerExprs.Count - 1);
                    result.Add(SyntaxFactory.InitializerExpression(
                        SyntaxKind.ComplexElementInitializerExpression,
                        SyntaxFactory.SeparatedList<ExpressionSyntax>(innerExprs, innerSeps)));
                }
            }
            return result;
        }
    }
}
