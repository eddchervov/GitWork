using System.Windows.Documents;
using GitWork.Core;

namespace GitWork.App;

public partial class DiffView
{
    private readonly Dictionary<int, IReadOnlyList<DiffSyntaxSpan>> _oldSyntax = [];
    private readonly Dictionary<int, IReadOnlyList<DiffSyntaxSpan>> _newSyntax = [];
    private readonly Dictionary<int, IReadOnlyList<InlineDiffSpan>> _oldChanges = [];
    private readonly Dictionary<int, IReadOnlyList<InlineDiffSpan>> _newChanges = [];

    private void PrepareHighlighting()
    {
        this._oldSyntax.Clear();
        this._newSyntax.Clear();
        this._oldChanges.Clear();
        this._newChanges.Clear();
        this.PrepareSyntax(this.Lines.Where(line => line.OldNumber.HasValue)
            .Select(line => (line.OldNumber!.Value, line.OldText)), this._oldSyntax);
        this.PrepareSyntax(this.Lines.Where(line => line.NewNumber.HasValue)
            .Select(line => (line.NewNumber!.Value, line.NewText)), this._newSyntax);

        var changedRows = 0;
        foreach (var line in this.Lines)
        {
            if (line.Kind == DiffLineKind.Equal)
            {
                changedRows = 0;
                continue;
            }

            if (line.Kind != DiffLineKind.Modified || ++changedRows > 32
                || line.OldText.Length > 4096 || line.NewText.Length > 4096)
            {
                continue;
            }

            var changes = InlineDiff.Compare(line.OldText, line.NewText);
            if (line.OldNumber is { } oldNumber)
            {
                this._oldChanges[oldNumber] = changes.OldSpans;
            }

            if (line.NewNumber is { } newNumber)
            {
                this._newChanges[newNumber] = changes.NewSpans;
            }
        }
    }

    private void PrepareSyntax(IEnumerable<(int Number, string Text)> lines, Dictionary<int, IReadOnlyList<DiffSyntaxSpan>> target)
    {
        var chunk = new List<(int Number, string Text)>();
        foreach (var line in lines)
        {
            if (chunk.Count > 0 && line.Number != chunk[^1].Number + 1)
            {
                this.HighlightChunk(chunk, target);
                chunk.Clear();
            }

            chunk.Add(line);
        }

        this.HighlightChunk(chunk, target);
    }

    private void HighlightChunk(IReadOnlyList<(int Number, string Text)> chunk, Dictionary<int, IReadOnlyList<DiffSyntaxSpan>> target)
    {
        if (chunk.Count == 0)
        {
            return;
        }

        var spans = DiffSyntaxHighlighter.Highlight(this.FilePath, chunk.Select(line => line.Text).ToArray());
        for (var index = 0; index < chunk.Count; index++)
        {
            target[chunk[index].Number] = spans[index];
        }
    }

    private void AppendCodeText(InlineCollection inlines, string text, int? number, bool oldSide, bool changed)
    {
        var syntaxMap = oldSide ? this._oldSyntax : this._newSyntax;
        var changesMap = oldSide ? this._oldChanges : this._newChanges;
        var syntax = number is { } syntaxNumber ? syntaxMap.GetValueOrDefault(syntaxNumber) ?? [] : [];
        var changes = changed && number is { } changeNumber ? changesMap.GetValueOrDefault(changeNumber) ?? [] : [];
        var boundaries = new SortedSet<int> { 0, text.Length };
        foreach (var span in syntax)
        {
            if (span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= text.Length)
            {
                boundaries.Add(span.Start);
                boundaries.Add(span.Start + span.Length);
            }
        }

        foreach (var span in changes)
        {
            if (span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= text.Length)
            {
                boundaries.Add(span.Start);
                boundaries.Add(span.Start + span.Length);
            }
        }

        var positions = boundaries.ToArray();
        var syntaxIndex = 0;
        var changeIndex = 0;
        for (var index = 1; index < positions.Length; index++)
        {
            var start = positions[index - 1];
            var end = positions[index];
            while (syntaxIndex < syntax.Count && syntax[syntaxIndex].Start + syntax[syntaxIndex].Length <= start)
            {
                syntaxIndex++;
            }

            while (changeIndex < changes.Count && changes[changeIndex].Start + changes[changeIndex].Length <= start)
            {
                changeIndex++;
            }

            var run = new Run(text[start..end]);
            if (syntaxIndex < syntax.Count && syntax[syntaxIndex].Start <= start
                && syntax[syntaxIndex].Start + syntax[syntaxIndex].Length >= end)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, syntax[syntaxIndex].ResourceKey);
            }

            if (changeIndex < changes.Count && changes[changeIndex].Start <= start
                && changes[changeIndex].Start + changes[changeIndex].Length >= end)
            {
                run.SetResourceReference(TextElement.BackgroundProperty, oldSide ? "DiffRemovedFragmentBrush" : "DiffAddedFragmentBrush");
            }

            inlines.Add(run);
        }
    }
}
