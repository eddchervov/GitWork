using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using GitWork.Core;

namespace GitWork.App;

public partial class DiffView : UserControl
{
    private static readonly Regex s_unifiedHunkHeader = new("^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static readonly DependencyProperty IsUnifiedProperty = DependencyProperty.Register(
        nameof(DiffView.IsUnified), typeof(bool), typeof(DiffView), new PropertyMetadata(false, DiffView.OnIsUnifiedChanged));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(DiffView.Header), typeof(object), typeof(DiffView));
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(DiffView.FilePath), typeof(string), typeof(DiffView), new PropertyMetadata(null, DiffView.OnFilePathChanged));

    public ObservableCollection<DiffLineView> Lines { get; } = [];

    public ObservableCollection<UnifiedDiffLineView> UnifiedLines { get; } = [];

    public int UnifiedHunkCount => this._unifiedHunks.Length;

    public int CurrentUnifiedHunkIndex => this.IsUnified
        ? this._currentChange
        : this._currentChange >= 0 && this._currentChange < this._rowHunks.Length
            ? this._rowHunks[this._currentChange]
            : -1;

    public event Action<int>? UnifiedHunkChanged;

    public object? Header
    {
        get => this.GetValue(DiffView.HeaderProperty);
        set => this.SetValue(DiffView.HeaderProperty, value);
    }

    public string? FilePath
    {
        get => (string?)this.GetValue(DiffView.FilePathProperty);
        set => this.SetValue(DiffView.FilePathProperty, value);
    }

    public bool IsUnified
    {
        get => (bool)this.GetValue(DiffView.IsUnifiedProperty);
        set => this.SetValue(DiffView.IsUnifiedProperty, value);
    }

    private List<int> _changeStarts = [];
    private int[] _unifiedHunks = [];
    private int[] _rawUnifiedHunks = [];
    private int[] _rowHunks = [];
    private int _currentChange = -1;
    private string _patch = "";
    private readonly List<FrameworkContentElement> _documentRows = [];

    public DiffView()
    {
        this.InitializeComponent();
    }

    public void ShowDiff(FileDiff diff)
    {
        this.ResetContext(diff);
        this.ShowDiffContent(diff);
        _ = this.LoadContextBoundsAsync();
    }

    private void ShowDiffContent(FileDiff diff)
    {
        this._patch = diff.UnifiedText;
        this._rawUnifiedHunks = this.BuildUnifiedLines(diff);
        this.InsertUnifiedContext();
        this.UpdateDiffStats();
        this._unifiedHunks = this.UnifiedLines.Select((line, index) => (line, index))
            .Where(item => item.line.Kind == UnifiedDiffLineKind.Hunk)
            .Select(item => item.index).ToArray();

        if (this.UnifiedLines.Count == 0 && diff.IsBinary && string.IsNullOrWhiteSpace(diff.Message))
        {
            this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Notice, null, null, L10n.T("Бинарный файл без текстового diff"), -1));
        }

        this.MessageText.Text = diff.Message is null ? "" : L10n.T(diff.Message);
        this.UnifiedToggle.Visibility = diff.UnifiedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.MessageBorder.Visibility = string.IsNullOrWhiteSpace(diff.Message) ? Visibility.Collapsed : Visibility.Visible;
        this.Lines.Clear();

        var displayLines = this.BuildContextLines(diff.Lines);
        foreach (var line in displayLines)
        {
            this.Lines.Add(new DiffLineView(line));
        }

        this._changeStarts = [];
        var previousWasChanged = false;

        for (var index = 0; index < this.Lines.Count; index++)
        {
            var isChanged = this.Lines[index].Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Modified;

            if (isChanged && !previousWasChanged)
            {
                this._changeStarts.Add(index);
            }

            previousWasChanged = isChanged;
        }

        this._rowHunks = this._changeStarts
            .Select(row => DiffView.FindUnifiedHunk(displayLines[row], this._rawUnifiedHunks, diff.UnifiedText))
            .ToArray();

        this.PrepareHighlighting();
        this.ApplyUnifiedMode();
        this.UpdateChangeControls();
        this.ScrollToCurrentChange();
        if (this._currentChange < 0)
        {
            this.UnifiedHunkChanged?.Invoke(this.CurrentUnifiedHunkIndex);
        }
    }

    private int[] BuildUnifiedLines(FileDiff diff)
    {
        this.UnifiedLines.Clear();
        var metadataLines = new List<string>();
        var oldNumber = 0;
        var newNumber = 0;
        var hunkIndex = -1;
        var rawHunkLines = new List<int>();
        var rawLineIndex = 0;

        foreach (var rawLine in diff.UnifiedText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var hunkMatch = DiffView.s_unifiedHunkHeader.Match(line);
            if (hunkMatch.Success)
            {
                if (hunkIndex < 0)
                {
                    foreach (var metadata in metadataLines.Where(DiffView.IsChangeMetadata))
                    {
                        this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Notice, null, null, metadata, -1));
                    }
                }

                hunkIndex++;
                rawHunkLines.Add(rawLineIndex);
                oldNumber = int.Parse(hunkMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                newNumber = int.Parse(hunkMatch.Groups[3].Value, CultureInfo.InvariantCulture);
                this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Hunk, null, null, line, hunkIndex));
                rawLineIndex++;
                continue;
            }

            if (hunkIndex < 0)
            {
                if (!string.IsNullOrWhiteSpace(line)
                    && !(diff.IsBinary && line.StartsWith("Бинарный файл.", StringComparison.Ordinal)))
                {
                    metadataLines.Add(line);
                }

                rawLineIndex++;
                continue;
            }

            if (line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Notice, null, null, L10n.T("Нет символа новой строки в конце файла"), hunkIndex));
                rawLineIndex++;
                continue;
            }

            if (line.Length == 0)
            {
                rawLineIndex++;
                continue;
            }

            var marker = line[0];
            var text = line[1..];
            if (marker == ' ')
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Context, oldNumber++, newNumber++, text, hunkIndex));
            }
            else if (marker == '-')
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Removed, oldNumber++, null, text, hunkIndex));
            }
            else if (marker == '+')
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Added, null, newNumber++, text, hunkIndex));
            }

            rawLineIndex++;
        }

        if (hunkIndex < 0 && diff.Lines.Any(line => line.Kind != DiffLineKind.Equal))
        {
            this.AppendSyntheticHunk(diff.Lines);
        }
        else if (hunkIndex < 0)
        {
            foreach (var line in metadataLines)
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Notice, null, null, line, -1));
            }
        }

        return rawHunkLines.ToArray();
    }

    private static bool IsChangeMetadata(string line) => line.StartsWith("old mode ", StringComparison.Ordinal)
        || line.StartsWith("new mode ", StringComparison.Ordinal)
        || line.StartsWith("new file mode ", StringComparison.Ordinal)
        || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
        || line.StartsWith("rename from ", StringComparison.Ordinal)
        || line.StartsWith("rename to ", StringComparison.Ordinal)
        || line.StartsWith("copy from ", StringComparison.Ordinal)
        || line.StartsWith("copy to ", StringComparison.Ordinal)
        || line.StartsWith("similarity index ", StringComparison.Ordinal)
        || line.StartsWith("dissimilarity index ", StringComparison.Ordinal);

    private void AppendSyntheticHunk(IReadOnlyList<DiffLine> lines)
    {
        this.UnifiedLines.Add(new UnifiedDiffLineView(UnifiedDiffLineKind.Hunk, null, null, L10n.T("Изменения файла"), 0));

        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Equal)
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(
                    UnifiedDiffLineKind.Context, line.OldNumber, line.NewNumber, line.OldText, 0));
                continue;
            }

            if (line.Kind is DiffLineKind.Removed or DiffLineKind.Modified)
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(
                    UnifiedDiffLineKind.Removed, line.OldNumber, null, line.OldText, 0));
            }

            if (line.Kind is DiffLineKind.Added or DiffLineKind.Modified)
            {
                this.UnifiedLines.Add(new UnifiedDiffLineView(
                    UnifiedDiffLineKind.Added, null, line.NewNumber, line.NewText, 0));
            }
        }
    }

    private static void OnIsUnifiedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not DiffView view || !view.IsInitialized)
        {
            return;
        }

        view.ApplyUnifiedMode();
        view._currentChange = -1;
        view.UpdateChangeControls();
        view.ScrollToCurrentChange();
        if (view._currentChange < 0)
        {
            view.UnifiedHunkChanged?.Invoke(view.CurrentUnifiedHunkIndex);
        }
    }

    private static void OnFilePathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is DiffView view && view.IsInitialized)
        {
            view.PrepareHighlighting();
            view.ApplyUnifiedMode();
        }
    }

    private void ApplyUnifiedMode()
    {
        this.BuildSelectableDocument();
        this.ColumnHeaders.Visibility = this.IsUnified || this.Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDiffStats()
    {
        var added = this.UnifiedLines.Count(line => line.Kind == UnifiedDiffLineKind.Added);
        var removed = this.UnifiedLines.Count(line => line.Kind == UnifiedDiffLineKind.Removed);
        this.AddedLinesText.Text = added > 0 ? $"+{added}" : "";
        this.RemovedLinesText.Text = removed > 0 ? $"−{removed}" : "";
        this.DiffStats.Visibility = added + removed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviousChange_Click(object sender, RoutedEventArgs e) => this.NavigateChange(-1);

    private void NextChange_Click(object sender, RoutedEventArgs e) => this.NavigateChange(1);

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } || e.Key != Key.F7)
        {
            return;
        }

        this.NavigateChange(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        e.Handled = true;
    }

    public void NavigateChange(int offset)
    {
        var count = this.IsUnified ? this._unifiedHunks.Length : this._changeStarts.Count;

        if (count == 0)
        {
            return;
        }

        var nextChange = this._currentChange < 0 ? 0 : this._currentChange + offset;
        this._currentChange = Math.Clamp(nextChange, 0, count - 1);
        this.UnifiedHunkChanged?.Invoke(this.CurrentUnifiedHunkIndex);
        this.UpdateChangeControls();
        this.ScrollToCurrentChange();
    }

    private void UpdateChangeControls()
    {
        var count = this.IsUnified ? this._unifiedHunks.Length : this._changeStarts.Count;
        var current = count == 0 || this._currentChange < 0 ? 0 : this._currentChange + 1;
        this.ChangeCounter.Text = $"{current}/{count}";
        this.ChangeCounter.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.PreviousButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.NextChangeButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.PreviousButton.IsEnabled = count > 0 && current > 1;
        this.NextChangeButton.IsEnabled = count > 0 && current < count;
    }

    private void ScrollToCurrentChange()
    {
        var count = this.IsUnified ? this._unifiedHunks.Length : this._changeStarts.Count;

        if (count == 0)
        {
            return;
        }

        if (this._currentChange < 0)
        {
            this._currentChange = 0;
            this.UpdateChangeControls();
        }

        this._currentChange = Math.Min(this._currentChange, count - 1);

        if (this.IsUnified)
        {
            this.UnifiedHunkChanged?.Invoke(this.CurrentUnifiedHunkIndex);
            this.ScrollDocumentRow(this._unifiedHunks[this._currentChange]);
            return;
        }

        this.ScrollDocumentRow(this._changeStarts[this._currentChange]);
        this.UnifiedHunkChanged?.Invoke(this.CurrentUnifiedHunkIndex);
    }

    private void CopyDiff_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(this._patch);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            this.MessageText.Text = L10n.T("Буфер обмена занят. Повторите копирование.");
            this.MessageBorder.Visibility = Visibility.Visible;
        }
    }

    private void ScrollDocumentRow(int index)
    {
        if (index >= 0 && index < this._documentRows.Count)
        {
            this._documentRows[index].BringIntoView();
        }
    }

    private void BuildSelectableDocument()
    {
        this._documentRows.Clear();
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = this.CodeViewer.FontFamily, FontSize = this.CodeViewer.FontSize };
        if (this.IsUnified)
        {
            for (var index = 0; index <= this.UnifiedLines.Count; index++)
            {
                this.AppendUnifiedContextControl(document, index);
                if (index == this.UnifiedLines.Count)
                {
                    break;
                }

                var line = this.UnifiedLines[index];
                var paragraph = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(8, 2, 8, 2) };
                var gutter = new Run($"{line.OldNumber,5} {line.NewNumber,5} {line.Marker,1} ");
                gutter.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                paragraph.Inlines.Add(gutter);
                var oldSide = line.Kind == UnifiedDiffLineKind.Removed;
                var number = line.Kind is UnifiedDiffLineKind.Hunk or UnifiedDiffLineKind.Notice ? null : oldSide ? line.OldNumber : line.NewNumber;
                this.AppendCodeText(paragraph.Inlines, line.Text, number, oldSide,
                    line.Kind is UnifiedDiffLineKind.Removed or UnifiedDiffLineKind.Added);
                if (line.Kind is UnifiedDiffLineKind.Added or UnifiedDiffLineKind.Removed)
                {
                    paragraph.SetResourceReference(TextElement.BackgroundProperty, line.Kind == UnifiedDiffLineKind.Added ? "DiffAddedBrush" : "DiffRemovedBrush");
                }
                else if (line.Kind is UnifiedDiffLineKind.Hunk or UnifiedDiffLineKind.Notice)
                {
                    paragraph.SetResourceReference(TextElement.BackgroundProperty, "RaisedBrush");
                }

                document.Blocks.Add(paragraph);
                this._documentRows.Add(paragraph);
            }
        }
        else
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
            table.Columns.Add(new TableColumn());
            table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            for (var index = 0; index <= this.Lines.Count; index++)
            {
                this.AppendSplitContextControl(group, index);
                if (index == this.Lines.Count)
                {
                    break;
                }

                var line = this.Lines[index];
                var row = new TableRow();
                var beforeParagraph = new Paragraph { Margin = new Thickness(0) };
                beforeParagraph.Inlines.Add(new Run($"{line.OldNumber,5}  "));
                this.AppendCodeText(beforeParagraph.Inlines, line.OldText, line.OldNumber, true, line.Kind == DiffLineKind.Modified);
                var afterParagraph = new Paragraph { Margin = new Thickness(0) };
                afterParagraph.Inlines.Add(new Run($"{line.NewNumber,5}  "));
                this.AppendCodeText(afterParagraph.Inlines, line.NewText, line.NewNumber, false, line.Kind == DiffLineKind.Modified);
                var before = new TableCell(beforeParagraph) { Padding = new Thickness(6, 1, 6, 1) };
                var after = new TableCell(afterParagraph) { Padding = new Thickness(6, 1, 6, 1), BorderBrush = (Brush)this.FindResource("BorderBrush"), BorderThickness = new Thickness(1, 0, 0, 0) };
                if (line.Kind is DiffLineKind.Removed or DiffLineKind.Modified)
                {
                    before.SetResourceReference(TextElement.BackgroundProperty, "DiffRemovedBrush");
                }

                if (line.Kind is DiffLineKind.Added or DiffLineKind.Modified)
                {
                    after.SetResourceReference(TextElement.BackgroundProperty, "DiffAddedBrush");
                }

                row.Cells.Add(before);
                row.Cells.Add(after);
                group.Rows.Add(row);
                this._documentRows.Add(before.Blocks.FirstBlock!);
            }

            document.Blocks.Add(table);
        }

        this.CodeViewer.Document = document;
    }

    private static int FindUnifiedHunk(DiffLine line, int[] hunkLines, string unifiedText)
    {
        var headers = unifiedText.Split('\n');
        for (var index = 0; index < hunkLines.Length; index++)
        {
            var match = DiffView.s_unifiedHunkHeader.Match(headers[hunkLines[index]]);
            if (!match.Success)
            {
                continue;
            }

            var oldStart = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            var newStart = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            var isInOldRange = line.OldNumber is { } oldNumber && oldCount > 0
                && oldNumber >= oldStart && oldNumber < oldStart + oldCount;
            var isInNewRange = line.NewNumber is { } newNumber && newCount > 0
                && newNumber >= newStart && newNumber < newStart + newCount;
            if (isInOldRange || isInNewRange)
            {
                return index;
            }
        }

        return hunkLines.Length == 0 ? -1 : 0;
    }
}

public enum UnifiedDiffLineKind
{
    Hunk,
    Context,
    Removed,
    Added,
    Notice
}

public sealed class UnifiedDiffLineView
{
    public UnifiedDiffLineKind Kind { get; }

    public int? OldNumber { get; }

    public int? NewNumber { get; }

    public string Text { get; }

    public string Marker => this.Kind switch
    {
        UnifiedDiffLineKind.Removed => "−",
        UnifiedDiffLineKind.Added => "+",
        _ => ""
    };

    public int HunkIndex { get; }

    public UnifiedDiffLineView(UnifiedDiffLineKind kind, int? oldNumber, int? newNumber, string text, int hunkIndex)
    {
        this.Kind = kind;
        this.OldNumber = oldNumber;
        this.NewNumber = newNumber;
        this.Text = text;
        this.HunkIndex = hunkIndex;
    }
}

public sealed class DiffLineView
{
    public DiffLineKind Kind { get; }

    public int? OldNumber { get; }

    public int? NewNumber { get; }

    public string OldText { get; }

    public string NewText { get; }

    public DiffLineView(DiffLine line)
    {
        this.Kind = line.Kind;
        this.OldNumber = line.OldNumber;
        this.NewNumber = line.NewNumber;
        this.OldText = line.OldText;
        this.NewText = line.NewText;
    }
}
