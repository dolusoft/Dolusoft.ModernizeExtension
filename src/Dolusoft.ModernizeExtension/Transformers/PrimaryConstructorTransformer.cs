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

internal sealed class PrimaryConstructorTransformer : ICodeTransformer
{
    public string Name => "Primary Constructor";

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

            var rewriter  = new Rewriter(semanticModel);
            var newRoot   = rewriter.Visit(root);
            if (newRoot == root) continue;

            var newDoc       = solution.WithDocumentSyntaxRoot(docId, newRoot).GetDocument(docId)!;
            var formattedDoc = await Formatter.FormatAsync(newDoc, cancellationToken: ct).ConfigureAwait(false);
            solution = formattedDoc.Project.Solution;
        }
        return solution;
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;

        public Rewriter(SemanticModel model) => _model = model;

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            node = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

            if (!TryExtractConvertibleConstructor(node, out var ctor, out var assignments))
                return node;

            // Build rename map: field name (_logger) → parameter name (logger)
            var renameMap = BuildRenameMap(assignments!);

            // Keep only members that are NOT the constructor and NOT fields being removed
            var newMembers = node.Members
                .Where(m => m != ctor)
                .Where(m => !(m is FieldDeclarationSyntax fd &&
                               fd.Declaration.Variables.All(v => renameMap.ContainsKey(v.Identifier.Text))))
                .ToList();

            // Rename field references to parameter names in remaining members
            var renamer    = new FieldRenamer(renameMap);
            var renamedMembers = newMembers
                .Select(m => (MemberDeclarationSyntax)renamer.Visit(m)!)
                .ToList();

            // Transfer identifier's trailing \r\n to open brace so params stay on same line
            var identTrailing = node.Identifier.TrailingTrivia;
            var cleanParams   = ctor!.ParameterList
                .WithLeadingTrivia()
                .WithCloseParenToken(ctor.ParameterList.CloseParenToken.WithTrailingTrivia());

            return node
                .WithIdentifier(node.Identifier.WithTrailingTrivia())
                .WithParameterList(cleanParams)
                .WithOpenBraceToken(node.OpenBraceToken.WithLeadingTrivia(identTrailing))
                .WithMembers(SyntaxFactory.List(renamedMembers));
        }

        private bool TryExtractConvertibleConstructor(
            ClassDeclarationSyntax classNode,
            out ConstructorDeclarationSyntax? ctor,
            out IReadOnlyList<AssignmentExpressionSyntax>? assignments)
        {
            ctor        = null;
            assignments = null;

            var ctors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            if (ctors.Count != 1) return false;

            ctor = ctors[0];
            if (ctor.Initializer != null) return false;
            if (ctor.Body == null) return false;

            var stmts = ctor.Body.Statements.ToList();
            if (stmts.Count == 0) return false;

            var assigns = new List<AssignmentExpressionSyntax>();
            foreach (var stmt in stmts)
            {
                if (stmt is not ExpressionStatementSyntax exprStmt) return false;
                if (exprStmt.Expression is not AssignmentExpressionSyntax assign) return false;
                if (assign.Right is not IdentifierNameSyntax) return false;
                assigns.Add(assign);
            }

            var paramNames = ctor.ParameterList.Parameters.Select(p => p.Identifier.Text).ToHashSet();
            var usedParams = assigns.Select(a => ((IdentifierNameSyntax)a.Right).Identifier.Text).ToHashSet();
            if (!paramNames.SetEquals(usedParams)) return false;

            // Only transform when every assignment targets a field, not a property.
            // Property assignments require a different approach (initializers) and
            // renaming a property-name-derived key corrupts type names and other-instance accesses.
            foreach (var assign in assigns)
            {
                var lhsNode = assign.Left switch
                {
                    MemberAccessExpressionSyntax mae when mae.Expression is ThisExpressionSyntax
                        => (SyntaxNode?)mae.Name,
                    IdentifierNameSyntax id => id,
                    _ => null
                };
                if (lhsNode == null) return false;
                if (_model.GetSymbolInfo(lhsNode).Symbol is not IFieldSymbol) return false;
            }

            assignments = assigns;
            return true;
        }

        private static Dictionary<string, string> BuildRenameMap(IReadOnlyList<AssignmentExpressionSyntax> assignments)
        {
            var map = new Dictionary<string, string>();
            foreach (var assignment in assignments)
            {
                var paramName = ((IdentifierNameSyntax)assignment.Right).Identifier.Text;
                var fieldName = assignment.Left switch
                {
                    MemberAccessExpressionSyntax mae => mae.Name.Identifier.Text,
                    IdentifierNameSyntax id          => id.Identifier.Text,
                    _                                => null
                };
                if (fieldName != null)
                    map[fieldName] = paramName;
            }
            return map;
        }
    }

    // Renames field identifier references within a syntax subtree.
    // Handles both `_logger` and `this._logger` forms.
    private sealed class FieldRenamer : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _map;

        public FieldRenamer(Dictionary<string, string> map) => _map = map;

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            // this._field  →  parameter name (full node replacement)
            if (node.Expression is ThisExpressionSyntax &&
                _map.TryGetValue(node.Name.Identifier.Text, out var paramName))
            {
                return SyntaxFactory.IdentifierName(
                    SyntaxFactory.Identifier(paramName).WithTriviaFrom(node.Name.Identifier))
                    .WithLeadingTrivia(node.GetLeadingTrivia());
            }

            // For all other member access expressions, only visit the receiver (Expression),
            // never the Name — otherwise VisitIdentifierName would rename property/method names
            // on other instances (e.g. other.FieldName → other.paramName).
            var newExpr = (ExpressionSyntax?)Visit(node.Expression) ?? node.Expression;
            return ReferenceEquals(newExpr, node.Expression) ? node : node.WithExpression(newExpr);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (_map.TryGetValue(node.Identifier.Text, out var paramName))
                return node.WithIdentifier(
                    SyntaxFactory.Identifier(paramName).WithTriviaFrom(node.Identifier));
            return base.VisitIdentifierName(node);
        }
    }
}
