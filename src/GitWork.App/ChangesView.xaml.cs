using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GitWork.Core;

namespace GitWork.App;

public partial class ChangesView : UserControl
{
    private static readonly Dictionary<string, CommitDraft> s_drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly GitIndexService _index;
    private readonly GitChangeService _changes;
    private readonly GitService _git;
    private readonly List<ChangeSource> _sources = [];
    private readonly List<ChangeFileEntry> _files = [];
    private readonly ICollectionView _filesView;
    private int _selectionVersion;
    private bool _isInitializing = true;
    private bool _isRefreshingFiles;
    private bool _canLoadFiles;
    private bool _isBusy;
    private bool _isReloading;
    private string? _draftRepository;
    private FileDiff? _shownDiff;
    private string? _shownFile;

    public ObservableCollection<ChangeGroup> Groups { get; } = [];

    public bool IsBusy => this._isBusy || this._isReloading;

    public bool CanLeave => !this.IsBusy;

    public void Detach()
    {
        this.SaveDraft();
        this._selectionVersion++;
        this._canLoadFiles = false;
        this._liveTimer.Stop();
    }

    public ChangesView(string title, IReadOnlyList<ChangeSource> sources, GitService git, string? selectedPath = null, string? selectedWorktree = null, bool autoRefresh = true)
    {
        this.InitializeComponent();
        this.DiffHeaderHost.Children.Remove(this.FileHeader);
        this.DiffViewer.Header = this.FileHeader;
        this.Unloaded += (_, _) => this.Detach();
        this._git = git;
        this._autoRefresh = autoRefresh;
        this._index = new GitIndexService(git);
        this._changes = new GitChangeService(git);
        this._sources.AddRange(sources.Select(source => source with { Path = Path.GetFullPath(source.Path) }));
        this.ReplaceFiles(new Dictionary<string, bool>());
        this._filesView = CollectionViewSource.GetDefaultView(this._files);
        this._filesView.Filter = this.MatchesCurrentFilter;
        this.FilesList.ItemsSource = this._filesView;
        this.RepositoryPicker.ItemsSource = this._sources;
        this.RepositoryPicker.Visibility = this._sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        this.RepositoryPicker.SelectedItem = this._sources.FirstOrDefault(source => selectedWorktree is not null
            && GitService.PathsEqual(source.Path, selectedWorktree)) ?? this._sources.FirstOrDefault();
        this._isInitializing = false;
        this.RestoreDraft();
        var initial = this._files.FirstOrDefault(entry => this.IsCurrentRepository(entry)
            && (selectedPath is null || string.Equals(entry.File.Path, selectedPath, StringComparison.Ordinal)));
        this.RefreshFiles(initial, true);
        this.UpdateCommitControls();
        this.Loaded += (_, _) =>
        {
            this._canLoadFiles = true;
            if (this.FilesList.SelectedItem is ChangeFileEntry entry)
            {
                this.LoadFileAsync(entry);
            }
        };
    }

    private void ReplaceFiles(IReadOnlyDictionary<string, bool> inclusion)
    {
        foreach (var entry in this._files)
        {
            entry.PropertyChanged -= this.File_PropertyChanged;
        }

        this._files.Clear();
        this.Groups.Clear();
        foreach (var source in this._sources)
        {
            var entries = source.Files.Select(file => new ChangeFileEntry(source, file)).ToArray();
            foreach (var entry in entries)
            {
                entry.IsIncluded = !inclusion.TryGetValue(ChangesView.FileKey(entry), out var included) || included;
                entry.PropertyChanged += this.File_PropertyChanged;
            }

            this.Groups.Add(new ChangeGroup(source.Name, source.Path, entries));
            this._files.AddRange(entries);
        }
    }

    private static string FileKey(ChangeFileEntry entry) => entry.Source.Path + "\0" + entry.File.Path + "\0" + entry.File.OriginalPath;

    private bool IsCurrentRepository(ChangeFileEntry entry) => this.RepositoryPicker.SelectedItem is ChangeSource source
        && GitService.PathsEqual(source.Path, entry.Source.Path);

    private bool MatchesCurrentFilter(object item) => item is ChangeFileEntry entry && this.IsCurrentRepository(entry)
        && SearchMatcher.Matches(this.SearchBox.Text, entry.File.DisplayPath, entry.File.Path);

    private void RepositoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isInitializing)
        {
            return;
        }

        this.SaveDraft();
        this.RestoreDraft();
        this.RefreshFiles(null, true);
        this.ShowCommitStatus("");
        this.UpdateCommitControls();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!this._isInitializing)
        {
            this.RefreshFiles(this.FilesList.SelectedItem as ChangeFileEntry, false);
        }
    }

    private void RefreshFiles(ChangeFileEntry? preferred, bool selectFirst)
    {
        this._isRefreshingFiles = true;
        this._filesView.Refresh();
        var visible = this._filesView.Cast<ChangeFileEntry>().ToArray();
        this.EmptyText.Text = L10n.T(this._files.Any(this.IsCurrentRepository) ? "Файлы не найдены" : "Изменённых файлов нет");
        this.EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var previous = this.FilesList.SelectedItem as ChangeFileEntry;
        var next = preferred is not null && visible.Contains(preferred) ? preferred : selectFirst ? visible.FirstOrDefault() : null;
        this.FilesList.SelectedItem = next;
        this._isRefreshingFiles = false;
        if (next is null)
        {
            this.ClearDetails();
        }
        else if (this._canLoadFiles && previous != next)
        {
            this.LoadFileAsync(next);
        }
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isRefreshingFiles || !this._canLoadFiles)
        {
            return;
        }

        if (this.FilesList.SelectedItem is ChangeFileEntry entry)
        {
            this.LoadFileAsync(entry);
        }
        else
        {
            this.ClearDetails();
        }
    }

    private void ClearDetails()
    {
        this._selectionVersion++;
        this.FileTitle.Text = L10n.T(this._files.Any(this.IsCurrentRepository) ? "Выберите файл" : "Нет изменений");
        this.FileContext.Text = "";
        this.LoadingText.Visibility = Visibility.Collapsed;
        this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, null));
        this._shownDiff = null;
        this._shownFile = null;
        this.DiffViewer.Visibility = Visibility.Collapsed;
        this.HistoryButton.IsEnabled = false;
    }

    private async void LoadFileAsync(ChangeFileEntry entry)
    {
        var version = ++this._selectionVersion;
        this.DiffViewer.Visibility = Visibility.Visible;
        this.FileTitle.Text = entry.File.DisplayPath;
        this.DiffViewer.FilePath = entry.File.Path;
        this.FileContext.Text = L10n.T(entry.File.StatusDescription);
        this.HistoryButton.IsEnabled = !this._isBusy;
        this.LoadingText.Visibility = Visibility.Visible;
        try
        {
            var diff = await this._changes.GetWorkingRevisionAsync(entry.Source.Path, entry.File);
            if (version != this._selectionVersion || this.FilesList.SelectedItem != entry || !this._canLoadFiles)
            {
                return;
            }

            if (this._shownFile != ChangesView.FileKey(entry) || this._shownDiff is not { } previousDiff
                || previousDiff.UnifiedText != diff.UnifiedText || previousDiff.IsBinary != diff.IsBinary || previousDiff.Message != diff.Message)
            {
                this.DiffViewer.ShowDiff(diff);
            }

            this._shownFile = ChangesView.FileKey(entry);
            this._shownDiff = diff;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (version == this._selectionVersion)
            {
                this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, exception.Message));
                this._shownDiff = null;
                this._shownFile = null;
            }
        }
        finally
        {
            if (version == this._selectionVersion)
            {
                this.LoadingText.Visibility = Visibility.Collapsed;
            }
        }
    }

    public ChangeSource? SelectedSource => (this.FilesList.SelectedItem as ChangeFileEntry)?.Source;

    public ChangedFile? SelectedFile => (this.FilesList.SelectedItem as ChangeFileEntry)?.File;

    public async Task RefreshAsync()
    {
        if (this.IsBusy)
        {
            return;
        }

        this._isReloading = true;
        this.UpdateCommitControls();
        try
        {
            await this.ReloadSourcesAsync();
        }
        finally
        {
            this._isReloading = false;
            this.UpdateCommitControls();
        }
    }

    private async Task ReloadSourcesAsync()
    {
        var refreshed = new List<ChangeSource>();
        foreach (var source in this._sources)
        {
            refreshed.Add(source with { Files = await this._git.GetStatusAsync(source.Path, true) });
        }

        var previous = this.FilesList.SelectedItem as ChangeFileEntry;
        var inclusion = this._files.ToDictionary(ChangesView.FileKey, entry => entry.IsIncluded, StringComparer.Ordinal);
        var currentRepository = (this.RepositoryPicker.SelectedItem as ChangeSource)?.Path;
        this._isRefreshingFiles = true;
        this._sources.Clear();
        this._sources.AddRange(refreshed);
        this.ReplaceFiles(inclusion);
        this._isInitializing = true;
        this.RepositoryPicker.ItemsSource = this._sources.ToArray();
        this.RepositoryPicker.SelectedItem = this._sources.FirstOrDefault(source => currentRepository is not null
            && GitService.PathsEqual(source.Path, currentRepository));
        this._isInitializing = false;
        this._isRefreshingFiles = false;
        var preferred = this._files.FirstOrDefault(entry => previous is not null
            && ChangesView.FileKey(entry) == ChangesView.FileKey(previous));
        this.RefreshFiles(preferred, true);
        this.UpdateCommitControls();
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangeFileEntry.IsIncluded))
        {
            this.UpdateCommitControls();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var entries = this._files.Where(this.IsCurrentRepository).ToArray();
        var include = entries.Any(entry => !entry.IsIncluded);
        foreach (var entry in entries)
        {
            entry.IsIncluded = include;
        }

        this.UpdateCommitControls();
    }

    private void CommitMessage_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!this._isInitializing)
        {
            this.SaveDraft();
            this.UpdateCommitControls();
        }
    }

    private void SaveDraft()
    {
        if (this._draftRepository is not null)
        {
            ChangesView.s_drafts[this._draftRepository] = new CommitDraft(this.CommitTitleBox.Text, this.CommitDescriptionBox.Text);
        }
    }

    private void RestoreDraft()
    {
        this._draftRepository = (this.RepositoryPicker.SelectedItem as ChangeSource)?.Path;
        var draft = this._draftRepository is not null && ChangesView.s_drafts.TryGetValue(this._draftRepository, out var saved)
            ? saved : new CommitDraft("", "");
        this._isInitializing = true;
        this.CommitTitleBox.Text = draft.Title;
        this.CommitDescriptionBox.Text = draft.Description;
        this._isInitializing = false;
    }

    private void UpdateCommitControls()
    {
        var entries = this._files.Where(this.IsCurrentRepository).ToArray();
        var count = entries.Count(entry => entry.IsIncluded);
        this.TitleText.Text = L10n.T("Изменённые файлы · {0}", entries.Length);
        this.CommitSelectionText.Text = L10n.T("Выбрано {0} из {1}", count, entries.Length);
        this.SelectAllBox.IsChecked = count == 0 ? false : count == entries.Length ? true : null;
        this.SelectAllBox.IsEnabled = !this._isBusy && entries.Length > 0;
        this.CommitButton.Content = this._isBusy ? L10n.T("Подождите…") : L10n.T("Создать коммит ({0})", count);
        this.CommitButton.IsEnabled = !this.IsBusy && count > 0 && !string.IsNullOrWhiteSpace(this.CommitTitleBox.Text);
    }

    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (this.IsBusy || this.RepositoryPicker.SelectedItem is not ChangeSource source
            || string.IsNullOrWhiteSpace(this.CommitTitleBox.Text))
        {
            return;
        }

        var files = this._files.Where(entry => this.IsCurrentRepository(entry) && entry.IsIncluded).Select(entry => entry.File).ToArray();
        if (files.Length == 0)
        {
            return;
        }

        var message = this.CommitTitleBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(this.CommitDescriptionBox.Text))
        {
            message += "\n\n" + this.CommitDescriptionBox.Text.Trim();
        }

        this.SetBusy(true);
        this.CommitProgress.Visibility = Visibility.Visible;
        this.ShowCommitStatus(L10n.T("Проверка выбранных файлов…"));
        var committed = false;
        try
        {
            var preview = await this._index.GetSelectedCommitPreviewAsync(source.Path, files);
            this.ShowCommitStatus(L10n.T("Создание коммита…"));
            await this._index.CommitSelectedAsync(preview, message);
            committed = true;
            this.CommitTitleBox.Clear();
            this.CommitDescriptionBox.Clear();
            this.ShowCommitStatus(L10n.T("Коммит создан."));
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this.ShowCommitStatus(L10n.T(exception.Message));
        }
        finally
        {
            try
            {
                await this.ReloadSourcesAsync();
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                this.ShowCommitStatus((committed ? L10n.T("Коммит создан. ") : "") + L10n.T("Не удалось обновить список: {0}", L10n.T(exception.Message)));
            }
            finally
            {
                this.CommitProgress.Visibility = Visibility.Collapsed;
                this.SetBusy(false);
            }
        }
    }

    private void ShowCommitStatus(string message)
    {
        this.CommitStatusText.Text = message;
        this.CommitStatusText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        this._isBusy = busy;
        this.FilesList.IsEnabled = !busy;
        this.RepositoryPicker.IsEnabled = !busy;
        this.SearchBox.IsEnabled = !busy;
        this.CommitTitleBox.IsEnabled = !busy;
        this.CommitDescriptionBox.IsEnabled = !busy;
        this.HistoryButton.IsEnabled = !busy && this.SelectedFile is not null;
        this.UpdateCommitControls();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F7 && Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
        {
            this.DiffViewer.NavigateChange(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            this.SearchBox.Focus();
            this.SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && this.SearchBox.IsKeyboardFocusWithin && this.SearchBox.Text.Length > 0)
        {
            this.SearchBox.Clear();
            e.Handled = true;
        }
    }

    private sealed record CommitDraft(string Title, string Description);
}

public sealed class ChangeGroup
{
    public string Title { get; }

    public string Path { get; }

    public IReadOnlyList<ChangeFileEntry> Files { get; }

    public ChangeGroup(string title, string path, IReadOnlyList<ChangeFileEntry> files)
    {
        this.Title = title;
        this.Path = path;
        this.Files = files;
    }
}

public sealed class ChangeFileEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isIncluded = true;
    private ChangeScope? _selectedScope;

    public ChangeSource Source { get; }

    public ChangedFile File { get; }

    public string RepositoryName => this.Source.Name;

    public string FullPath => System.IO.Path.Combine(this.Source.Path, this.File.Path);

    public string FileName => System.IO.Path.GetFileName(this.File.Path);

    public string StatusCode => this.File.Status == "??" ? "A"
        : this.File.Status.Trim() is { Length: > 0 } status ? status[..1] : "M";

    public bool HasStaged => this.File.Status.Length > 0 && this.File.Status[0] != ' ' && this.File.Status != "??";

    public bool HasWorking => this.File.Status == "??" || (this.File.Status.Length > 1 && this.File.Status[1] != ' ');

    public bool IsIncluded
    {
        get => this._isIncluded;
        set
        {
            if (this._isIncluded == value)
            {
                return;
            }

            this._isIncluded = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.IsIncluded)));
        }
    }

    public bool IsSelected
    {
        get => this._isSelected;
        set
        {
            if (this._isSelected == value)
            {
                return;
            }

            this._isSelected = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.IsSelected)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.IsWorkingSelected)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.IsStagedSelected)));
        }
    }

    public ChangeScope? SelectedScope
    {
        get => this._selectedScope;
        set
        {
            if (this._selectedScope == value)
            {
                return;
            }

            this._selectedScope = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.SelectedScope)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.IsWorkingSelected)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangeFileEntry.IsStagedSelected)));
        }
    }

    public bool IsWorkingSelected => this.IsSelected && this.SelectedScope == ChangeScope.WorkingTree;

    public bool IsStagedSelected => this.IsSelected && this.SelectedScope == ChangeScope.Staged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChangeFileEntry(ChangeSource source, ChangedFile file)
    {
        this.Source = source;
        this.File = file;
    }

    public bool IsAvailable(ChangeScope scope) => scope == ChangeScope.WorkingTree ? this.HasWorking : this.HasStaged;
}
