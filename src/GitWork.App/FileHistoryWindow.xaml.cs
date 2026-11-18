using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GitWork.Core;

namespace GitWork.App;

public partial class FileHistoryWindow : Window
{
    private const int PageSize = 50;
    private readonly string _worktree;
    private readonly string _relativeFile;
    private readonly string _currentPath;
    private readonly FileHistoryService _history;
    private readonly ObservableCollection<FileHistoryEntry> _entries = [];
    private int _selectionVersion;
    private bool _isLoadingPage;
    private bool _hasLoaded;

    public FileHistoryWindow(string worktree, string relativeFile, GitService git, string? currentPath = null)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this._worktree = Path.GetFullPath(worktree);
        this._relativeFile = relativeFile;
        this._currentPath = currentPath ?? relativeFile;
        this._history = new FileHistoryService(git);
        this.Title = L10n.T("История файла · {0}", Path.GetFileName(this._currentPath));
        this.FilePathText.Text = this._currentPath;
        this.HistoryList.ItemsSource = this._entries;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (this._hasLoaded)
        {
            return;
        }

        this._hasLoaded = true;
        await this.LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (this._isLoadingPage)
        {
            return;
        }

        this._isLoadingPage = true;
        this.MoreButton.IsEnabled = false;
        this.HistoryStatusText.Text = L10n.T(this._entries.Count == 0 ? "Загрузка истории…" : "Загрузка…");

        try
        {
            var page = await this._history.GetHistoryAsync(this._worktree, this._relativeFile, this._entries.Count, FileHistoryWindow.PageSize);
            foreach (var entry in page)
            {
                this._entries.Add(entry);
            }

            this.MoreButton.Visibility = page.Count == FileHistoryWindow.PageSize && this._entries.Count < 500
                ? Visibility.Visible
                : Visibility.Collapsed;
            this.HistoryStatusText.Text = this._entries.Count == 0
                ? L10n.T("История этого файла не найдена.")
                : this._entries.Count >= 500 ? L10n.T("Показаны последние 500 версий.") : L10n.T("Версий: {0}", this._entries.Count);

            if (this.HistoryList.SelectedItem is null && this._entries.Count > 0)
            {
                this.HistoryList.SelectedIndex = 0;
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this.HistoryStatusText.Text = L10n.T("Не удалось загрузить историю: {0}", L10n.T(FileHistoryWindow.ShortError(exception)));
        }
        finally
        {
            this._isLoadingPage = false;
            this.MoreButton.IsEnabled = true;
        }
    }

    private async void More_Click(object sender, RoutedEventArgs e) => await this.LoadPageAsync();

    private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.HistoryList.SelectedItem is not FileHistoryEntry entry)
        {
            return;
        }

        var version = ++this._selectionVersion;
        this.CommitTitle.Text = entry.Subject;
        this.CommitTitle.ToolTip = $"{entry.ShortHash} · {entry.Subject}";
        this.RevisionPathText.Text = $"{entry.Author} · {entry.DisplayDate}";
        this.RevisionPathText.ToolTip = L10n.T("Путь в этой версии: {0}", entry.RevisionPath);
        this.LoadingText.Text = L10n.T("Чтение версии и сравнений…");
        this.LoadingText.Visibility = Visibility.Visible;
        this.SnapshotText.Document.Blocks.Clear();
        this.SnapshotNotice.Text = "";
        this.CommitDiffViewer.ShowDiff(new FileDiff("", "", "", [], false, L10n.T("Чтение изменений коммита…")));
        this.WorkingDiffViewer.ShowDiff(new FileDiff("", "", "", [], false, L10n.T("Сравнение с текущим файлом…")));

        try
        {
            var snapshotTask = this._history.GetSnapshotAsync(this._worktree, entry);
            var commitDiffTask = this._history.GetCommitDiffAsync(this._worktree, entry);
            var workingDiffTask = this._history.GetWorkingDiffAsync(this._worktree, this._relativeFile, entry, this._currentPath);
            await Task.WhenAll(snapshotTask, commitDiffTask, workingDiffTask);
            if (version != this._selectionVersion || this.HistoryList.SelectedItem is not FileHistoryEntry selected || selected != entry)
            {
                return;
            }

            var snapshot = await snapshotTask;
            this.ShowSnapshot(snapshot.Text, entry.RevisionPath);
            this.SnapshotNotice.Text = snapshot.Message ?? "";
            this.CommitDiffViewer.FilePath = entry.RevisionPath;
            this.WorkingDiffViewer.FilePath = this._currentPath;
            this.CommitDiffViewer.ShowDiff(await commitDiffTask);
            this.WorkingDiffViewer.ShowDiff(await workingDiffTask);
            this.LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (version == this._selectionVersion)
            {
                var message = FileHistoryWindow.ShortError(exception);
                this.SnapshotNotice.Text = message;
                this.CommitDiffViewer.ShowDiff(new FileDiff("", "", "", [], false, message));
                this.WorkingDiffViewer.ShowDiff(new FileDiff("", "", "", [], false, message));
                this.LoadingText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowSnapshot(string text, string path)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var syntax = DiffSyntaxHighlighter.Highlight(path, lines);
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var longestLine = "";
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            var line = lines[index];
            var expanded = line.Replace("\t", "        ");
            if (expanded.Length > longestLine.Length)
            {
                longestLine = expanded;
            }

            var position = 0;
            foreach (var span in syntax[index])
            {
                if (span.Start > position)
                {
                    paragraph.Inlines.Add(new Run(line[position..span.Start]));
                }

                var run = new Run(line.Substring(span.Start, span.Length));
                run.SetResourceReference(TextElement.ForegroundProperty, span.ResourceKey);
                paragraph.Inlines.Add(run);
                position = span.Start + span.Length;
            }

            if (position < line.Length)
            {
                paragraph.Inlines.Add(new Run(line[position..]));
            }
        }

        var width = new FormattedText(longestLine, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(this.SnapshotText.FontFamily, FontStyles.Normal, FontWeights.Normal,
                FontStretches.Normal), this.SnapshotText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            .WidthIncludingTrailingWhitespace;

        this.SnapshotText.Document = new FlowDocument(paragraph)
        {
            FontFamily = this.SnapshotText.FontFamily,
            FontSize = this.SnapshotText.FontSize,
            PagePadding = new Thickness(8),
            PageWidth = Math.Max(200, width + 32),
            ColumnWidth = double.PositiveInfinity
        };
        this.SnapshotText.Document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        this.SnapshotText.ScrollToHome();
    }

    private static string ShortError(Exception exception) => exception.Message.Length <= 180
        ? exception.Message
        : exception.Message[..177] + "…";
}
