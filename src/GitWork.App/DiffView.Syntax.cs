using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace GitWork.App;

public partial class DiffView
{
    private static readonly Regex s_csharpTokenRegex = new(
        "(?<comment>//.*|/\\*.*?\\*/)|"
        + "(?<string>\\$?\"\"\".*?\"\"\"|(?:\\$@|@\\$|@)\"(?:\"\"|[^\"\\r\\n])*\"|\\$?\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*')|"
        + "(?<keyword>\\b(?:abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|file|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|private|protected|public|readonly|record|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while|with|yield)\\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private bool ShouldHighlight(UnifiedDiffLineKind kind) => this.IsCSharpFile
        && kind is not (UnifiedDiffLineKind.Hunk or UnifiedDiffLineKind.Notice);

    private bool IsCSharpFile => this.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;

    private void AppendCodeText(InlineCollection inlines, string text, bool shouldHighlight)
    {
        if (!shouldHighlight)
        {
            inlines.Add(new Run(text));
            return;
        }

        var position = 0;
        foreach (Match match in DiffView.s_csharpTokenRegex.Matches(text))
        {
            if (match.Index > position)
            {
                inlines.Add(new Run(text[position..match.Index]));
            }

            var run = new Run(match.Value);
            var resourceKey = match.Groups["keyword"].Success
                ? "SyntaxKeywordBrush"
                : match.Groups["string"].Success
                    ? "SyntaxStringBrush"
                    : "SyntaxCommentBrush";
            run.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
            inlines.Add(run);
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(new Run(text[position..]));
        }
    }
}
