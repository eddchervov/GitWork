using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GitWork.App;

public static class DiffSyntaxHighlighter
{
    private const int MaximumSourceLength = 500_000;

    public static IReadOnlyList<DiffSyntaxSpan>[] Highlight(string? filePath, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = new IReadOnlyList<DiffSyntaxSpan>[lines.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = Array.Empty<DiffSyntaxSpan>();
        }

        if (!DiffSyntaxHighlighter.IsCSharpFile(filePath))
        {
            return result;
        }

        var sourceLength = Math.Max(0, lines.Count - 1);
        foreach (var line in lines)
        {
            sourceLength += line.Length;
            if (sourceLength > DiffSyntaxHighlighter.MaximumSourceLength)
            {
                return result;
            }
        }

        var sourceBuilder = new StringBuilder(sourceLength);
        var lineStarts = new int[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                sourceBuilder.Append('\n');
            }

            lineStarts[index] = sourceBuilder.Length;
            sourceBuilder.Append(lines[index]);
        }

        var source = sourceBuilder.ToString();
        var spans = new List<DiffSyntaxSpan>[lines.Count];

        try
        {
            var tree = CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse, SourceCodeKind.Script));
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
            {
                var resourceKey = DiffSyntaxHighlighter.GetTokenResourceKey(token);
                if (resourceKey is not null && token.Span.Length > 0)
                {
                    DiffSyntaxHighlighter.AddSpan(spans, lineStarts, lines, token.SpanStart, token.Span.Length, resourceKey);
                }
            }

            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
            {
                var resourceKey = DiffSyntaxHighlighter.GetTriviaResourceKey(trivia);
                if (resourceKey is not null && trivia.Span.Length > 0)
                {
                    DiffSyntaxHighlighter.AddSpan(spans, lineStarts, lines, trivia.SpanStart, trivia.Span.Length, resourceKey);
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return result;
        }

        for (var index = 0; index < spans.Length; index++)
        {
            result[index] = spans[index] is { } lineSpans
                ? lineSpans.OrderBy(span => span.Start).ToArray()
                : Array.Empty<DiffSyntaxSpan>();
        }

        return result;
    }

    private static bool IsCSharpFile(string? filePath)
    {
        var extension = Path.GetExtension(filePath ?? string.Empty);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csx", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetTokenResourceKey(SyntaxToken token)
    {
        if (token.IsKind(SyntaxKind.IdentifierToken))
        {
            return DiffSyntaxHighlighter.GetIdentifierResourceKey(token);
        }

        if (SyntaxFacts.IsKeywordKind(token.Kind()) || SyntaxFacts.IsContextualKeyword(token.Kind()))
        {
            return "SyntaxKeywordBrush";
        }

        if (token.IsKind(SyntaxKind.NumericLiteralToken))
        {
            return "SyntaxNumberBrush";
        }

        var kindName = token.Kind().ToString();
        if (token.IsKind(SyntaxKind.CharacterLiteralToken)
            || kindName is "StringLiteralToken" or "Utf8StringLiteralToken" or "InterpolatedStringTextToken"
            || kindName.Contains("RawStringLiteralToken", StringComparison.Ordinal)
            || kindName.StartsWith("Interpolated", StringComparison.Ordinal)
                && (kindName.EndsWith("StringStartToken", StringComparison.Ordinal)
                    || kindName.EndsWith("StringEndToken", StringComparison.Ordinal)))
        {
            return "SyntaxStringBrush";
        }

        return null;
    }

    private static string? GetIdentifierResourceKey(SyntaxToken token)
    {
        var parent = token.Parent;
        if (parent is null)
        {
            return null;
        }

        if (parent is MethodDeclarationSyntax method && method.Identifier == token
            || parent is LocalFunctionStatementSyntax localFunction && localFunction.Identifier == token
            || parent is ConstructorDeclarationSyntax constructor && constructor.Identifier == token
            || parent is DestructorDeclarationSyntax destructor && destructor.Identifier == token
            || parent is OperatorDeclarationSyntax operatorDeclaration && operatorDeclaration.OperatorToken == token)
        {
            return "SyntaxMethodBrush";
        }

        if (parent.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
            .Any(invocation => DiffSyntaxHighlighter.IsInvocationNameToken(invocation, token)))
        {
            return "SyntaxMethodBrush";
        }

        if (DiffSyntaxHighlighter.IsTypePosition(token))
        {
            return token.Text is "var" or "dynamic" ? "SyntaxKeywordBrush" : "SyntaxTypeBrush";
        }

        if (parent is PropertyDeclarationSyntax property && property.Identifier == token
            || parent is EventDeclarationSyntax eventDeclaration && eventDeclaration.Identifier == token)
        {
            return "SyntaxPropertyBrush";
        }

        if (parent.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(memberAccess => memberAccess.Name.Span.Contains(token.Span)))
        {
            return "SyntaxPropertyBrush";
        }

        var identifier = token.ValueText;
        if (identifier.Length > 0 && char.IsUpper(identifier[0]))
        {
            return "SyntaxTypeBrush";
        }

        return null;
    }

    private static bool IsInvocationNameToken(InvocationExpressionSyntax invocation, SyntaxToken token)
    {
        var identifier = invocation.Expression switch
        {
            IdentifierNameSyntax identifierName => identifierName.Identifier,
            GenericNameSyntax genericName => genericName.Identifier,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier,
            _ => default
        };

        return identifier.RawKind != 0 && identifier == token;
    }

    private static bool IsTypePosition(SyntaxToken token)
    {
        foreach (var type in token.Parent?.AncestorsAndSelf().OfType<TypeSyntax>() ?? [])
        {
            var parent = type.Parent;
            if (parent is VariableDeclarationSyntax variable && variable.Type == type
                || parent is ParameterSyntax parameter && parameter.Type == type
                || parent is MethodDeclarationSyntax method && method.ReturnType == type
                || parent is LocalFunctionStatementSyntax localFunction && localFunction.ReturnType == type
                || parent is PropertyDeclarationSyntax property && property.Type == type
                || parent is EventDeclarationSyntax eventDeclaration && eventDeclaration.Type == type
                || parent is DelegateDeclarationSyntax delegateDeclaration && delegateDeclaration.ReturnType == type
                || parent is BaseTypeSyntax baseType && baseType.Type == type
                || parent is ObjectCreationExpressionSyntax objectCreation && objectCreation.Type == type
                || parent is TypeOfExpressionSyntax typeOfExpression && typeOfExpression.Type == type
                || parent is DefaultExpressionSyntax defaultExpression && defaultExpression.Type == type
                || parent is CastExpressionSyntax castExpression && castExpression.Type == type
                || parent is DeclarationExpressionSyntax declarationExpression && declarationExpression.Type == type
                || parent is TypeConstraintSyntax typeConstraint && typeConstraint.Type == type
                || parent is CatchDeclarationSyntax catchDeclaration && catchDeclaration.Type == type
                || parent is ForEachStatementSyntax forEach && forEach.Type == type
                || parent is AttributeSyntax attribute && attribute.Name == type
                || parent is TypeArgumentListSyntax typeArguments && typeArguments.Arguments.Any(argument => argument.Span.Contains(token.Span)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetTriviaResourceKey(SyntaxTrivia trivia)
    {
        if (trivia.IsDirective)
        {
            return "SyntaxDirectiveBrush";
        }

        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
        {
            return "SyntaxCommentBrush";
        }

        return null;
    }

    private static void AddSpan(
        List<DiffSyntaxSpan>[] spans,
        int[] lineStarts,
        IReadOnlyList<string> lines,
        int sourceStart,
        int sourceLength,
        string resourceKey)
    {
        var sourceEnd = sourceStart + sourceLength;
        var lineIndex = Array.BinarySearch(lineStarts, sourceStart);
        if (lineIndex < 0)
        {
            lineIndex = ~lineIndex - 1;
        }

        for (var index = Math.Max(lineIndex, 0); index < lines.Count; index++)
        {
            var lineStart = lineStarts[index];
            if (lineStart >= sourceEnd)
            {
                break;
            }

            var start = Math.Max(sourceStart, lineStart);
            var end = Math.Min(sourceEnd, lineStart + lines[index].Length);
            if (end > start)
            {
                (spans[index] ??= []).Add(new DiffSyntaxSpan(start - lineStart, end - start, resourceKey));
            }
        }
    }
}
