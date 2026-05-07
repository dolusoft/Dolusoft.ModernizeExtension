using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dolusoft.ModernizeExtension.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Dolusoft.ModernizeExtension.Transformers;

internal sealed class CollectionExpressionTransformer : ICodeTransformer
{
    public string Name => "Collection Expressions";

    public async Task<Solution> TransformAsync(Solution solution, IReadOnlyList<DocumentId> scope, CancellationToken ct)
    {
        foreach (var docId in scope)
        {
            ct.ThrowIfCancellationRequested();
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;

            // Collection expressions require C# 12+ (LanguageVersion value 1200).
            // Default (0) and large sentinel values (Latest, Preview) mean "use latest" — allow those.
            var parseOptions = doc.Project.ParseOptions as CSharpParseOptions;
            var langVer = (int)(parseOptions?.LanguageVersion ?? 0);
            if (langVer > 0 && langVer < 1200) continue;

            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) continue;
            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model == null) continue;

            var rewriter = new Rewriter(model);
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

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            node = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;

            if (node.Declaration.Variables.Count != 1) return node;
            var declarator = node.Declaration.Variables[0];
            var initValue  = declarator.Initializer?.Value;
            if (initValue == null) return node;

            if (!TryGetItems(initValue, out var items)) return node;

            var typeInfo   = _model.GetTypeInfo(initValue);
            var targetType = typeInfo.Type ?? typeInfo.ConvertedType;
            if (targetType == null || targetType is IErrorTypeSymbol) return node;
            if (!SupportsCollectionExpression(targetType)) return node;

            var collectionExpr = BuildCollectionExpression(items!);

            // When declared with `var`, replace with the explicit type so that
            // `var x = new List<T> { ... }` becomes `List<T> x = [...]`.
            var declaredType = node.Declaration.Type;
            TypeSyntax newType = declaredType;
            if (declaredType.IsVar)
            {
                var minimalName = targetType.ToMinimalDisplayString(_model, node.SpanStart);
                newType = SyntaxFactory.ParseTypeName(minimalName).WithTriviaFrom(declaredType);
            }

            var newDeclarator = declarator.WithInitializer(
                declarator.Initializer!.WithValue(collectionExpr));

            return node.WithDeclaration(
                node.Declaration
                    .WithType(newType)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(newDeclarator)));
        }

        // Extracts the flat list of expressions from a collection / array initializer.
        // Returns false for Dictionary-style complex initializers ({ {k,v}, {k,v} })
        // because those cannot be expressed as a simple [k, v] collection expression.
        private static bool TryGetItems(ExpressionSyntax initValue, out List<ExpressionSyntax>? items)
        {
            items = null;

            InitializerExpressionSyntax? init = initValue switch
            {
                ObjectCreationExpressionSyntax oce
                    when oce.Initializer?.IsKind(SyntaxKind.CollectionInitializerExpression) == true
                    => oce.Initializer,
                ImplicitObjectCreationExpressionSyntax ioe
                    when ioe.Initializer?.IsKind(SyntaxKind.CollectionInitializerExpression) == true
                    => ioe.Initializer,
                ArrayCreationExpressionSyntax ace when ace.Initializer != null
                    => ace.Initializer,
                ImplicitArrayCreationExpressionSyntax iace
                    => iace.Initializer,
                _ => null
            };

            if (init == null) return false;

            var result = new List<ExpressionSyntax>();
            foreach (var expr in init.Expressions)
            {
                if (expr is InitializerExpressionSyntax) return false;
                result.Add(expr);
            }

            items = result;
            return true;
        }

        // A type supports collection expressions when it is an array or implements
        // IEnumerable<T> — the same set of types that already accepted { } initializers.
        private static bool SupportsCollectionExpression(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol) return true;

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.IsGenericType) continue;
                var def = iface.OriginalDefinition;
                if (def.Name == "IEnumerable" &&
                    def.ContainingNamespace?.ToString() == "System.Collections.Generic")
                    return true;
            }
            return false;
        }

        private static CollectionExpressionSyntax BuildCollectionExpression(List<ExpressionSyntax> items)
        {
            if (items.Count == 0)
                return SyntaxFactory.CollectionExpression(
                    SyntaxFactory.SeparatedList<CollectionElementSyntax>());

            var elements = items
                .Select(e => (CollectionElementSyntax)SyntaxFactory.ExpressionElement(
                    e.WithoutLeadingTrivia()))
                .ToList();

            var seps = Enumerable.Repeat(
                SyntaxFactory.Token(SyntaxKind.CommaToken)
                    .WithTrailingTrivia(SyntaxFactory.Space),
                elements.Count - 1);

            return SyntaxFactory.CollectionExpression(
                SyntaxFactory.SeparatedList<CollectionElementSyntax>(elements, seps));
        }
    }
}
