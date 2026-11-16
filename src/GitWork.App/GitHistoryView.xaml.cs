using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class GitHistoryView : UserControl
{
    private readonly GitGraphService _graph;
    private readonly GitRemoteService _remote;
    private readonly GitCommitDetailsService _details;
    private readonly GitService _git;
    private readonly GitCommitOperationService _operations;
    private readonly AppSettings _preparationSettings;
    private RemoteRepositoryState? _remoteState;
    private bool _isBusy;
    private bool _isInitializing = true;
    private bool _isClosed;
    private int _limit = 200;
    private int _detailsVersion;
    private int _fileVersion;
    private readonly HashSet<string> _expandedMerges = new(StringComparer.Ordinal);
    private bool _showAllBranches;
    private bool _hasMoreGraph;
    private int _loadVersion;
    private GitCommitOperationResult? _cherryState;

    public GitHistoryView(IReadOnlyList<GraphRepositoryChoice> repositories, GitService git, AppSettings? settings = null)
    {
        this.InitializeComponent();
        ((Grid)this.CommitFileHeader.Parent).Children.Remove(this.CommitFileHeader);
        this.CommitDiff.Header = this.CommitFileHeader;
        this._graph = new GitGraphService(git);
        this._remote = new GitRemoteService(git);
        this._details = new GitCommitDetailsService(git);
        this._git = git;
        this._operations = new GitCommitOperationService(git);
        this._preparationSettings = settings ?? AppSettings.Load();
        this.CommitDiff.IsUnified = true;
        this.GraphModePicker.ItemsSource = new[] { new GraphModeOption(false, L10n.T("Компактно")), new GraphModeOption(true, L10n.T("Все ветки")) };
        this.GraphModePicker.SelectedIndex = 0;
        this.RepositoryPicker.ItemsSource = repositories;
        if (repositories.Count == 1)
        {
            this.RepositoryPicker.Visibility = Visibility.Collapsed;
            this.Toolbar.ColumnDefinitions[0].Width = new GridLength(0);
        }
        this.RepositoryPicker.SelectedIndex = repositories.Count > 0 ? 0 : -1;
        this.RepositoryPicker.ToolTip = this.SelectedRepository?.Path;
        this._isInitializing = false;
        this.SetBusy(false);
    }

    private GraphRepositoryChoice? SelectedRepository => this.RepositoryPicker.SelectedItem as GraphRepositoryChoice;

    public bool IsBusy => this._isBusy;

    public bool CanLeave => !this._isBusy;

    public event EventHandler? RepositoryStateChanged;

    public event EventHandler? BusyChanged;

    public Task RefreshAsync() => this.ReloadAsync();

    internal FrameworkElement TakeToolbar()
    {
        ((Grid)this.Toolbar.Parent).Children.Remove(this.Toolbar);
        this.Toolbar.Margin = new Thickness(0);
        this.Toolbar.ColumnDefinitions[1].MinWidth = 0;
        this.Toolbar.ColumnDefinitions[1].Width = GridLength.Auto;

        return this.Toolbar;
    }

    public void Detach()
    {
        this._isClosed = true;
        this._loadVersion++;
        this._detailsVersion++;
        this._fileVersion++;
    }

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        this._isClosed = false;
        await this.ReloadAsync();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e) => this.Detach();

    private async void Repository_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this._isInitializing || !this.IsLoaded || this._isBusy)
        {
            return;
        }

        this._limit = 200;
        this._expandedMerges.Clear();
        this._detailsVersion++;
        this._fileVersion++;
        this.CommitsList.ItemsSource = null;
        this._remoteState = null;
        this.RemotePicker.ItemsSource = null;
        this.RepositoryPicker.ToolTip = this.SelectedRepository?.Path;
        this.UpdateRemoteSummary();
        this.ClearDetails();
        await this.ReloadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await this.ReloadAsync();

    private async void GraphMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this._isInitializing || this._isBusy || this.GraphModePicker.SelectedItem is not GraphModeOption mode)
        {
            return;
        }

        this._showAllBranches = mode.ShowAll;
        this._limit = 200;
        await this.ReloadAsync();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => this.ApplyCommitSearch();

    private bool ApplyCommitSearch()
    {
        var query = this.SearchBox.Text.Trim();
        this.SearchResultText.Text = "";
        if (query.Length == 0)
        {
            if (this.CommitsList.SelectedItem is null)
            {
                this.CommitsList.SelectedItem = this.CommitsList.Items.OfType<GraphCommitItem>().FirstOrDefault(item => item.Row.Commit.IsHead)
                    ?? this.CommitsList.Items.OfType<GraphCommitItem>().FirstOrDefault();
            }

            return false;
        }

        var match = this.CommitsList.Items.OfType<GraphCommitItem>().FirstOrDefault(item => SearchMatcher.Matches(
            query, item.Row.Commit.Subject, item.Row.Commit.Author, item.Row.Commit.ShortHash, item.Row.Commit.Hash,
            string.Join(" ", item.Row.Commit.References)));
        if (match is null)
        {
            this.CommitsList.SelectedItem = null;
            this.SearchResultText.Text = L10n.T("Совпадений нет");

            return true;
        }

        this.CommitsList.SelectedItem = match;
        this.CommitsList.ScrollIntoView(match);

        return true;
    }

    private async void MergeToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (this._isBusy || this._showAllBranches || sender is not FrameworkElement { DataContext: GraphCommitItem item })
        {
            return;
        }

        var hash = item.Row.Commit.Hash;
        if (!item.IsExpanded && this._expandedMerges.Count >= GitGraphService.MaximumExpandedMerges)
        {
            this.OperationStatus.Text = L10n.T("Можно раскрыть до {0} слияний. Сверните одну из открытых веток.", GitGraphService.MaximumExpandedMerges);
            return;
        }

        if (item.IsExpanded)
        {
            this._expandedMerges.Remove(hash);
        }
        else
        {
            this._expandedMerges.Add(hash);
        }

        this.CommitsList.SelectedItem = item;
        await this.ReloadAsync();
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        this._limit = Math.Min(1000, this._limit + 200);
        await this.ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (this._isBusy || this.SelectedRepository is not { } repository)
        {
            return;
        }

        this.SetBusy(true);
        this.OperationStatus.Text = L10n.T("Чтение локальной истории…");
        try
        {
            await this.LoadAsync(repository);
            this.OperationStatus.Text = this._cherryState?.IsInProgress == true ? this._cherryState.Message : "";
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            this._remoteState = null;
            this.RemotePicker.ItemsSource = null;
            this.UpdateRemoteSummary();
            this.OperationStatus.Text = exception.Message;
            this.EmptyGraph.Text = this.CommitsList.Items.Count == 0 ? L10n.T("Не удалось прочитать историю") : "";
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    private async Task LoadAsync(GraphRepositoryChoice repository)
    {
        var version = ++this._loadVersion;
        var rows = this._showAllBranches ? await this._graph.GetAsync(repository.Path, this._limit)
            : await this._graph.GetCompactAsync(repository.Path, this._limit, this._expandedMerges);
        if (this._isClosed || version != this._loadVersion)
        {
            return;
        }

        var selectedHash = (this.CommitsList.SelectedItem as GraphCommitItem)?.Row.Commit.Hash;
        var scroll = ViewState.ScrollOffset(this.CommitsList);
        if (!this._showAllBranches)
        {
            this._expandedMerges.IntersectWith(rows.Select(row => row.Commit.Hash));
        }

        var laneCount = rows.Select(row => Math.Max(Math.Max(row.TopLaneCount, row.BottomLaneCount), row.NodeLane + 1)).DefaultIfEmpty(1).Max();
        var width = Math.Clamp(laneCount * 14 + 30, 44, 140);
        this.CommitsList.ItemsSource = rows.Select(row => new GraphCommitItem(row, width, laneCount, !this._showAllBranches,
            this._expandedMerges.Contains(row.Commit.Hash))).ToArray();
        this.HistoryCount.Text = L10n.T("Коммитов: {0}", rows.Count);
        this.EmptyGraph.Text = L10n.T(this._showAllBranches ? "В репозитории пока нет коммитов" : "У текущей ветки пока нет коммитов");
        this.EmptyGraph.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this._hasMoreGraph = this._showAllBranches ? rows.Count >= this._limit
            : rows.Any(row => row.HasMoreMainline);
        this.CommitsList.SelectedItem = this.CommitsList.Items.OfType<GraphCommitItem>().FirstOrDefault(item => item.Row.Commit.Hash == selectedHash)
            ?? this.CommitsList.Items.OfType<GraphCommitItem>().FirstOrDefault(item => item.Row.Commit.IsHead)
            ?? this.CommitsList.Items.OfType<GraphCommitItem>().FirstOrDefault();
        if (!this.ApplyCommitSearch())
        {
            if (selectedHash is null && this.CommitsList.SelectedItem is { } selected)
            {
                this.CommitsList.ScrollIntoView(selected);
            }
            else
            {
                ViewState.RestoreScroll(this.CommitsList, scroll);
            }
        }

        try
        {
            var remoteState = await this._remote.ReadAsync(repository.Path);
            if (this._isClosed || version != this._loadVersion)
            {
                return;
            }

            this._remoteState = remoteState;
            var selectedRemote = (this.RemotePicker.SelectedItem as GitRemoteInfo)?.Name;
            this.RemotePicker.ItemsSource = this._remoteState.Remotes;
            this.RemotePicker.SelectedItem = this._remoteState.Remotes.FirstOrDefault(remote => remote.Name == selectedRemote)
                ?? this._remoteState.Remotes.FirstOrDefault(remote => remote.Name == this._remoteState.UpstreamRemote)
                ?? this._remoteState.Remotes.FirstOrDefault();
            this.UpdateRemoteSummary();
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            if (this._isClosed || version != this._loadVersion)
            {
                return;
            }

            this._remoteState = null;
            this.RemotePicker.ItemsSource = null;
            this.UpdateRemoteSummary(exception.Message);
        }

        if (this._isClosed || version != this._loadVersion)
        {
            return;
        }

        this._cherryState = await this._operations.ReadCherryPickStateAsync(repository.Path);
        if (!this._isClosed && version == this._loadVersion)
        {
            this.UpdateCherryState();
        }
    }

    private void UpdateRemoteSummary(string? error = null)
    {
        var state = this._remoteState;
        var hasUpstream = state?.UpstreamRef is not null && !state.IsDetached;
        this.SyncCounters.Visibility = hasUpstream ? Visibility.Visible : Visibility.Collapsed;
        this.RemoteSelector.Visibility = state?.Remotes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        this.BranchSummary.Text = L10n.T(error is not null ? "Синхронизация недоступна"
            : state?.IsDetached == true ? "HEAD без ветки"
            : state is { UpstreamRef: null } ? "Нет upstream" : "");
        this.BranchSummary.ToolTip = error is null ? L10n.T("Upstream — ветка на сервере, с которой синхронизируется текущая ветка.") : L10n.T(error);
        if (!hasUpstream || state is null)
        {
            return;
        }

        var upstream = state.UpstreamRemote + "/" + state.UpstreamBranch?.Replace("refs/heads/", "", StringComparison.Ordinal);
        this.OutgoingCount.Text = state.Ahead.ToString();
        this.IncomingCount.Text = state.Behind.ToString();
        this.OutgoingCounter.ToolTip = L10n.T("{0} → {1}: коммиты к отправке. По данным последнего Fetch.", state.Branch, upstream);
        this.IncomingCounter.ToolTip = L10n.T("{0} → {1}: коммиты к получению. По данным последнего Fetch.", upstream, state.Branch);
    }

    private void SetBusy(bool busy)
    {
        this._isBusy = busy;
        this.Toolbar.IsEnabled = !busy;
        this.CommitsList.IsEnabled = !busy;
        this.CommitActions.IsEnabled = !busy;
        this.GraphModePicker.IsEnabled = !busy;
        this.RefreshButton.IsEnabled = !busy;
        this.MoreButton.IsEnabled = !busy && this._limit < 1000 && this._hasMoreGraph;
        this.FetchButton.IsEnabled = !busy && this.RemotePicker.SelectedItem is GitRemoteInfo;
        this.PullButton.IsEnabled = !busy && this._remoteState?.CanPull == true;
        this.PullButton.ToolTip = this._remoteState?.CanPull == true
            ? L10n.T("Загрузить изменения upstream и обновить текущую ветку только fast-forward.")
            : L10n.T(this._remoteState?.PullUnavailableReason ?? "Выберите репозиторий");
        this.BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Remote_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!this._isInitializing)
        {
            this.FetchButton.IsEnabled = !this._isBusy && this.RemotePicker.SelectedItem is GitRemoteInfo;
            this.RemotePicker.ToolTip = (this.RemotePicker.SelectedItem as GitRemoteInfo)?.Url;
            this.FetchButton.ToolTip = this.RemotePicker.SelectedItem is GitRemoteInfo remote
                ? L10n.T("Fetch: {0}\n{1}\nЗагрузить коммиты и обновить сведения о ветках. Рабочие файлы не меняются.", remote.Name, remote.Url)
                : L10n.T("В репозитории не настроен сервер для Fetch.");
        }
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (this.RemotePicker.SelectedItem is GitRemoteInfo remote)
        {
            await this.SynchronizeAsync(false, remote.Name);
        }
    }

    private async void Pull_Click(object sender, RoutedEventArgs e) => await this.SynchronizeAsync(true, null);

    private async Task SynchronizeAsync(bool pull, string? remoteName)
    {
        if (this._isBusy || this.SelectedRepository is not { } repository)
        {
            return;
        }

        this.SetBusy(true);
        this.OperationStatus.Text = pull ? L10n.T("Pull: проверка и загрузка upstream…") : L10n.T("Fetch: загрузка {0}…", remoteName);
        string resultMessage;
        try
        {
            var result = pull ? await this._remote.PullAsync(repository.Path)
                : await this._remote.FetchAsync(repository.Path, remoteName!);
            resultMessage = result.Message;
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            resultMessage = exception.Message;
        }

        try
        {
            await this.LoadAsync(repository);
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            this._remoteState = null;
            this.RemotePicker.ItemsSource = null;
            this.UpdateRemoteSummary(exception.Message);
            resultMessage += "\n" + L10n.T("Не удалось обновить граф: {0}", L10n.T(exception.Message));
        }
        finally
        {
            this.OperationStatus.Text = resultMessage;
            this.SetBusy(false);
        }

        this.RepositoryStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void Commit_Changed(object sender, SelectionChangedEventArgs e)
    {
        var version = ++this._detailsVersion;
        this._fileVersion++;
        this.ClearDetails();
        if (this.SelectedRepository is not { } repository || this.CommitsList.SelectedItem is not GraphCommitItem item)
        {
            return;
        }

        var commit = item.Row.Commit;
        this.CommitSummary.Text = commit.Subject;
        this.CommitSummary.ToolTip = commit.Subject;
        this.CommitMetadata.Text = $"{commit.ShortHash} · {item.DateLabel} · {commit.Author}"
            + (commit.Parents.Count > 1 ? L10n.T(" · относительно первого родителя") : "");
        this.CommitMetadata.ToolTip = commit.Hash;
        try
        {
            var files = await this._details.GetFilesAsync(repository.Path, commit.Hash);
            if (version != this._detailsVersion || this._isClosed)
            {
                return;
            }

            this.CommitFiles.ItemsSource = files.Select(file => new GraphChangedFile(file)).ToArray();
            this.FilesCount.Text = files.Count.ToString();
            this.CommitFiles.SelectedIndex = files.Count > 0 ? 0 : -1;
            if (files.Count == 0)
            {
                this.CommitDiff.ShowDiff(new FileDiff("", "", "", [], false, L10n.T("В этом коммите нет изменений файлов.")));
            }
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            if (version == this._detailsVersion && !this._isClosed)
            {
                this.CommitDiff.ShowDiff(new FileDiff("", "", "", [], false, exception.Message));
            }
        }
    }

    private async void File_Changed(object sender, SelectionChangedEventArgs e)
    {
        var version = ++this._fileVersion;
        if (this.SelectedRepository is not { } repository || this.CommitsList.SelectedItem is not GraphCommitItem item
            || this.CommitFiles.SelectedItem is not GraphChangedFile row)
        {
            return;
        }

        var file = row.File;
        this.FileHistoryButton.IsEnabled = true;
        this.CommitDiff.ShowDiff(new FileDiff("", "", "", [], false, L10n.T("Чтение изменений коммита…")));
        this.FileTitle.Text = file.DisplayPath;
        this.CommitDiff.FilePath = file.Path;
        try
        {
            var diff = await this._details.GetDiffAsync(repository.Path, item.Row.Commit.Hash, file);
            if (version == this._fileVersion && !this._isClosed)
            {
                this.CommitDiff.ShowDiff(diff);
            }
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            if (version == this._fileVersion && !this._isClosed)
            {
                this.CommitDiff.ShowDiff(new FileDiff("", "", "", [], false, exception.Message));
            }
        }
    }

    private void ClearDetails()
    {
        this.CommitFiles.ItemsSource = null;
        this.CommitSummary.Text = L10n.T("Выберите коммит");
        this.CommitMetadata.Text = "";
        this.FilesCount.Text = "";
        this.FileTitle.Text = L10n.T("Выберите файл");
        this.FileHistoryButton.IsEnabled = false;
        this.CommitDiff.ShowDiff(new FileDiff("", "", "", [], false, null));
    }

    private static bool IsExpected(Exception exception) => exception is GitException or IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception;
}
