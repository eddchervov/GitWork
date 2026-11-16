using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using GitWork.Core;

namespace GitWork.App;

public partial class DiffView
{
    private FileDiff? _contextDiff;
    private DiffContext? _context;
    private Task<DiffContext>? _pendingContext;
    private List<ContextGap>? _contextGaps;
    private int _contextVersion;
    private bool _isLoadingContext;

    private void ResetContext(FileDiff diff)
    {
        this._contextVersion++;
        this._contextDiff = diff;
        this._context = null;
        this._pendingContext = null;
        this._contextGaps = null;
        this._isLoadingContext = false;
        this._currentChange = -1;
    }

    private async Task LoadContextBoundsAsync()
    {
        if (this._contextGaps is null)
        {
            return;
        }

        var version = this._contextVersion;
        try
        {
            var context = await this.GetContextAsync();
            if (version != this._contextVersion)
            {
                return;
            }

            this.ValidateContext(context);
            this._context = context;
            var offset = this.CodeViewer.VerticalOffset;
            this.BuildSelectableDocument();
            this.CodeViewer.ScrollToVerticalOffset(offset);
        }
        catch (Exception exception) when (exception is GitException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (version == this._contextVersion)
            {
                // Ошибку фонового чтения показываем только при явном запросе раскрытия строк.
                this._pendingContext = null;
            }
        }
    }

    private Task<DiffContext> GetContextAsync() => this._context is { } context
        ? Task.FromResult(context)
        : this._pendingContext ??= this._contextDiff!.LoadContextAsync!();

    private void InsertUnifiedContext()
    {
        if (this._contextDiff?.LoadContextAsync is null || this._contextDiff.IsBinary
            || this._contextDiff.Lines.Count > 0 && (this._contextDiff.Lines.All(line => line.Kind == DiffLineKind.Added)
                || this._contextDiff.Lines.All(line => line.Kind == DiffLineKind.Removed)))
        {
            return;
        }

        this._contextGaps ??= this.CreateContextGaps();
        var offset = 0;
        foreach (var gap in this._contextGaps)
        {
            var index = gap.UnifiedIndex + offset;
            foreach (var line in this.GetLeadingContext(gap))
            {
                this.UnifiedLines.Insert(index++, new UnifiedDiffLineView(UnifiedDiffLineKind.Context,
                    line.OldNumber, line.NewNumber, line.NewText, -1));
                offset++;
            }

            gap.DisplayUnifiedIndex = index;
            foreach (var line in this.GetTrailingContext(gap))
            {
                this.UnifiedLines.Insert(index++, new UnifiedDiffLineView(UnifiedDiffLineKind.Context,
                    line.OldNumber, line.NewNumber, line.NewText, -1));
                offset++;
            }
        }
    }

    private List<ContextGap> CreateContextGaps()
    {
        var gaps = new List<ContextGap>();
        var oldEnd = 0;
        var newEnd = 0;
        var lines = this._contextDiff!.Lines;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.OldNumber is { } oldNumber && line.NewNumber is { } newNumber
                && oldNumber - oldEnd > 1 && oldNumber - oldEnd == newNumber - newEnd)
            {
                var unifiedIndex = this.UnifiedLines.ToList().FindIndex(item => item.OldNumber == oldNumber && item.NewNumber == newNumber);
                if (unifiedIndex > 0 && this.UnifiedLines[unifiedIndex - 1].Kind == UnifiedDiffLineKind.Hunk)
                {
                    unifiedIndex--;
                }

                gaps.Add(new ContextGap(index, Math.Max(0, unifiedIndex), oldEnd + 1, newEnd + 1,
                    oldNumber - oldEnd - 1));
            }

            oldEnd = line.OldNumber ?? oldEnd;
            newEnd = line.NewNumber ?? newEnd;
        }

        gaps.Add(new ContextGap(lines.Count, this.UnifiedLines.Count, oldEnd + 1, newEnd + 1, null));

        return gaps;
    }

    private IReadOnlyList<DiffLine> BuildContextLines(IReadOnlyList<DiffLine> original)
    {
        if (this._contextGaps is null)
        {
            return original;
        }

        var result = original.ToList();
        var offset = 0;
        foreach (var gap in this._contextGaps)
        {
            var leading = this.GetLeadingContext(gap);
            var trailing = this.GetTrailingContext(gap);
            result.InsertRange(gap.RowIndex + offset, leading);
            offset += leading.Count;
            gap.DisplayRowIndex = gap.RowIndex + offset;
            result.InsertRange(gap.RowIndex + offset, trailing);
            offset += trailing.Count;
        }

        return result;
    }

    private List<DiffLine> GetLeadingContext(ContextGap gap) => this.GetContextRows(gap, 0, gap.LeadingShown);

    private List<DiffLine> GetTrailingContext(ContextGap gap) => this.GetContextRows(gap,
        (gap.Count ?? 0) - gap.TrailingShown, gap.TrailingShown);

    private List<DiffLine> GetContextRows(ContextGap gap, int start, int count)
    {
        var rows = new List<DiffLine>(count);
        if (this._context is null)
        {
            return rows;
        }

        for (var index = start; index < start + count; index++)
        {
            var oldNumber = gap.OldStart + index;
            var newNumber = gap.NewStart + index;
            rows.Add(new DiffLine(oldNumber, this._context.OldLines[oldNumber - 1], newNumber,
                this._context.NewLines[newNumber - 1], DiffLineKind.Equal));
        }

        return rows;
    }

    private void AppendUnifiedContextControl(FlowDocument document, int index)
    {
        var gap = this._contextGaps?.FirstOrDefault(item => item.DisplayUnifiedIndex == index && item.HasHiddenLines);
        if (gap is not null)
        {
            document.Blocks.Add(new BlockUIContainer(this.CreateContextControl(gap)) { Margin = new Thickness(0) });
        }
    }

    private void AppendSplitContextControl(TableRowGroup group, int index)
    {
        var gap = this._contextGaps?.FirstOrDefault(item => item.DisplayRowIndex == index && item.HasHiddenLines);
        if (gap is not null)
        {
            var cell = new TableCell(new BlockUIContainer(this.CreateContextControl(gap))) { ColumnSpan = 2, Padding = new Thickness(0) };
            var row = new TableRow();
            row.Cells.Add(cell);
            group.Rows.Add(row);
        }
    }

    private FrameworkElement CreateContextControl(ContextGap gap)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        if (gap.RowIndex > 0)
        {
            this.AddContextButton(panel, gap, "↓ " + L10n.T("20 строк"), 20, false, "Показать следующие неизменённые строки");
        }

        if (gap.RowIndex < this._contextDiff!.Lines.Count)
        {
            this.AddContextButton(panel, gap, "↑ " + L10n.T("20 строк"), 20, true, "Показать предыдущие неизменённые строки");
        }

        this.AddContextButton(panel, gap, "↕ " + L10n.T("Все строки"), int.MaxValue, false,
            "Показать все строки этого промежутка");
        var count = gap.Count is { } total
            ? L10n.T("Скрыто строк: {0}", total - gap.LeadingShown - gap.TrailingShown)
            : L10n.T("Неизменённые строки");
        var label = new TextBlock { Text = this._isLoadingContext ? L10n.T("Загрузка…") : count,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(label);
        var border = new Border { Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");

        return border;
    }

    private void AddContextButton(Panel panel, ContextGap gap, string text, int count, bool fromEnd, string toolTip)
    {
        var button = new Button { Content = text, ToolTip = L10n.T(toolTip), Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 0), IsEnabled = !this._isLoadingContext };
        button.Click += async (_, _) => await this.ExpandContextAsync(gap, count, fromEnd);
        panel.Children.Add(button);
    }

    private async Task ExpandContextAsync(ContextGap gap, int count, bool fromEnd)
    {
        if (this._isLoadingContext || this._contextDiff?.LoadContextAsync is null)
        {
            return;
        }

        var version = this._contextVersion;
        var verticalOffset = this.CodeViewer.VerticalOffset;
        this._isLoadingContext = true;
        this.BuildSelectableDocument();
        try
        {
            var context = await this.GetContextAsync();
            if (version != this._contextVersion)
            {
                return;
            }

            this.ValidateContext(context);
            this._context = context;
            var remaining = gap.Count!.Value - gap.LeadingShown - gap.TrailingShown;
            var added = Math.Min(count, remaining);
            if (fromEnd)
            {
                gap.TrailingShown += added;
            }
            else
            {
                gap.LeadingShown += added;
            }

            this.ShowDiffContent(this._contextDiff);
        }
        catch (Exception exception) when (exception is GitException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (version == this._contextVersion)
            {
                this.MessageText.Text = L10n.T(exception.Message);
                this.MessageBorder.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (version == this._contextVersion)
            {
                this._isLoadingContext = false;
                this.BuildSelectableDocument();
                this.CodeViewer.ScrollToVerticalOffset(verticalOffset);
            }
        }
    }

    private void ValidateContext(DiffContext context)
    {
        foreach (var line in this._contextDiff!.Lines)
        {
            if (line.OldNumber is { } oldNumber && (oldNumber < 1 || oldNumber > context.OldLines.Count || context.OldLines[oldNumber - 1] != line.OldText)
                || line.NewNumber is { } newNumber && (newNumber < 1 || newNumber > context.NewLines.Count || context.NewLines[newNumber - 1] != line.NewText))
            {
                throw new GitException("Файл изменился. Обновите diff перед раскрытием строк.");
            }
        }

        foreach (var gap in this._contextGaps!)
        {
            var oldCount = context.OldLines.Count - gap.OldStart + 1;
            var newCount = context.NewLines.Count - gap.NewStart + 1;
            var count = gap.Count ?? oldCount;
            if (count < 0 || gap.Count is null && oldCount != newCount || count > oldCount || count > newCount)
            {
                throw new GitException("Контекст файла не совпадает с diff. Обновите сравнение.");
            }

            for (var index = 0; index < count; index++)
            {
                if (context.OldLines[gap.OldStart + index - 1] != context.NewLines[gap.NewStart + index - 1])
                {
                    throw new GitException("Контекст файла не совпадает с diff. Обновите сравнение.");
                }
            }

            gap.Count = count;
        }
    }

    private sealed class ContextGap(int rowIndex, int unifiedIndex, int oldStart, int newStart, int? count)
    {
        public int RowIndex { get; } = rowIndex;
        public int UnifiedIndex { get; } = unifiedIndex;
        public int OldStart { get; } = oldStart;
        public int NewStart { get; } = newStart;
        public int? Count { get; set; } = count;
        public int LeadingShown { get; set; }
        public int TrailingShown { get; set; }
        public int DisplayRowIndex { get; set; }
        public int DisplayUnifiedIndex { get; set; }
        public bool HasHiddenLines => this.Count is { } count && this.LeadingShown + this.TrailingShown < count;
    }
}
