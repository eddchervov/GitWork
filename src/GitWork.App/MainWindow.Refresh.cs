using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.Core;

namespace GitWork.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Dictionary<string, WorktreeState> _copyStates = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFullRefresh = DateTime.MinValue;
    private long _stateVersion;
    private bool _isRefreshing;
    private bool _isClosed;
    private bool _pendingDetailRefresh;

    private void InitializeRefresh()
    {
        this._refreshTimer.Tick += async (_, _) => await this.RefreshLiveAsync();
        this.Activated += async (_, _) => await this.RefreshLiveAsync();
        this.Closed += (_, _) =>
        {
            this._isClosed = true;
            this._stateVersion++;
            this._refreshTimer.Stop();
        };
    }

    private void AutoRefresh_Click(object sender, RoutedEventArgs e)
    {
        this._settings.AutoRefresh = this.AutoRefreshMenuItem.IsChecked;
        this.ConfigureRefresh();
        this.SaveSettings();
    }

    private void ConfigureRefresh()
    {
        this.AutoRefreshMenuItem.IsChecked = this._settings.AutoRefresh;

        if (this._settings.AutoRefresh)
        {
            this._refreshTimer.Start();
        }
        else
        {
            this._refreshTimer.Stop();
        }
    }

    private async Task<Dictionary<string, WorktreeState>> ReadStatesAsync(IEnumerable<WorktreeInfo> copies)
    {
        var result = new ConcurrentDictionary<string, WorktreeState>(StringComparer.OrdinalIgnoreCase);
        var distinct = copies.DistinctBy(tree => Path.GetFullPath(tree.Path), StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(distinct, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (tree, _) =>
        {
            result[Path.GetFullPath(tree.Path)] = await WorktreeStateReader.ReadAsync(tree, this._git);
        });

        return new Dictionary<string, WorktreeState>(result, StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateNavigationStates()
    {
        foreach (var node in NavigationBuilder.Flatten(this._navigation))
        {
            if (node.Worktree is { } tree)
            {
                this._copyStates.TryGetValue(Path.GetFullPath(tree.Path), out var state);
                node.Status = NavigationStatus.Combine([tree.IsPrunable || tree.IsBare ? new WorktreeState(tree, [], tree.Reason ?? "Копия недоступна") : state]);
            }
            else if (node.Workspace is { } workspace)
            {
                var paths = workspace.WorktreePaths.Values;
                var missing = workspace.RepositoryPaths.Count(path => !workspace.WorktreePaths.Keys.Any(key => GitService.PathsEqual(key, path)));
                node.Status = NavigationStatus.Combine(paths.Select(path => this._copyStates.GetValueOrDefault(Path.GetFullPath(path)) ?? MainWindow.MissingCopy(path)), missing);
            }
            else if (node.Kind == "Repository")
            {
                node.Status = NavigationStatus.Combine(node.Children.Where(child => child.Worktree is not null)
                    .Select(child => this._copyStates.GetValueOrDefault(Path.GetFullPath(child.Worktree!.Path))));
            }
        }
    }

    private async Task RefreshLiveAsync()
    {
        if (this._isClosed || !this.IsLoaded || !this.IsActive || !this.IsEnabled || !this._settings.AutoRefresh
            || this._isBusy || !this.CanLeaveGitClients || this.GitClientHost.Visibility == Visibility.Visible || this._isRefreshing || this.WindowState == WindowState.Minimized)
        {
            return;
        }

        this._isRefreshing = true;
        var version = this._stateVersion;
        var selectedKey = this._displayedNavigationKey;
        var workspace = this._activeWorkspace;
        var current = this._worktree;

        try
        {
            var full = DateTime.UtcNow - this._lastFullRefresh > TimeSpan.FromSeconds(45);
            var repositories = full ? this._repositories : this._repositories.Where(repository => workspace is not null
                ? workspace.WorktreePaths.Keys.Any(path => GitService.PathsEqual(path, repository.Path))
                : this._repository is not null && GitService.PathsEqual(repository.Path, this._repository.Path)).ToArray();
            var registered = new Dictionary<string, IReadOnlyList<WorktreeInfo>>(this._registeredTrees, StringComparer.OrdinalIgnoreCase);

            foreach (var repository in repositories)
            {
                try
                {
                    registered[repository.Path] = await this._git.GetWorktreesAsync(repository.Path);
                }
                catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
                {
                    registered[repository.Path] = (registered.GetValueOrDefault(repository.Path) ?? [])
                        .Select(tree => tree with { IsPrunable = true, Reason = exception.Message }).ToArray();
                }
            }

            var trees = repositories.SelectMany(repository => registered.GetValueOrDefault(repository.Path) ?? []);
            var states = await this.ReadStatesAsync(trees);

            if (this._isClosed || this._isBusy || version != this._stateVersion || selectedKey != this._displayedNavigationKey || !this.IsActive)
            {
                return;
            }

            var structureChanged = !MainWindow.TreeSignature(this._registeredTrees).Equals(MainWindow.TreeSignature(registered), StringComparison.Ordinal);
            var selectedChanged = current is not null && (this._pendingDetailRefresh || states.TryGetValue(Path.GetFullPath(current.Path), out var selectedState)
                && this._copyStates.GetValueOrDefault(Path.GetFullPath(current.Path))?.Signature != selectedState.Signature);
            var bundleChanged = workspace is not null && workspace.WorktreePaths.Values.Any(path => states.TryGetValue(Path.GetFullPath(path), out var state)
                && this._copyStates.GetValueOrDefault(Path.GetFullPath(path))?.Signature != state.Signature);

            this._registeredTrees = registered;

            if (full || structureChanged)
            {
                var validPaths = registered.Values.SelectMany(copies => copies).Select(tree => Path.GetFullPath(tree.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var path in this._copyStates.Keys.Where(path => !validPaths.Contains(path)).ToArray())
                {
                    this._copyStates.Remove(path);
                }
            }

            foreach (var pair in states)
            {
                this._copyStates[pair.Key] = pair.Value;
            }

            if (full)
            {
                this._lastFullRefresh = DateTime.UtcNow;
            }

            if (structureChanged)
            {
                var navigationOffset = ViewState.ScrollOffset(this.RepositoryTree);
                this.ApplyNavigationFilter();
                ViewState.RestoreScroll(this.RepositoryTree, navigationOffset);
            }

            this.UpdateNavigationStates();
            this.NavigationCount.Text = L10n.T("Worktree: {0} · комплектов: {1}", registered.Values.Sum(copies => copies.Count(tree => !tree.IsMain && !tree.IsBare)), this._settings.Workspaces.Count);

            if (this._displayedNavigationKey != selectedKey)
            {
                return;
            }

            if (current is not null && (selectedChanged || structureChanged))
            {
                this._pendingDetailRefresh = true;
                var updated = registered.Values.SelectMany(copies => copies).FirstOrDefault(tree => GitService.PathsEqual(tree.Path, current.Path));

                if (updated is null)
                {
                    if (workspace is not null)
                    {
                        this.CloseWorktreeActions_Click(this, new RoutedEventArgs());
                        this.ApplyBundleRows(MainWindow.CreateOverviewRows(workspace, this._copyStates));
                    }
                    else
                    {
                        this.ClearNavigationDetails(L10n.T("Копия больше не зарегистрирована"), L10n.T("Выберите рабочую копию слева"));
                    }

                    return;
                }

                var repositoryTrees = registered.Values.FirstOrDefault(copies => copies.Any(copy => GitService.PathsEqual(copy.Path, updated.Path))) ?? [];
                var branches = updated.IsPrunable || updated.IsBare ? [] : await this._git.GetBranchesAsync(updated.Path, repositoryTrees);

                if (version != this._stateVersion || this._isBusy || selectedKey != this._displayedNavigationKey || this._isClosed || !this.IsActive || !this.IsEnabled)
                {
                    return;
                }

                if (this.InlineGitHost.Visibility == Visibility.Visible)
                {
                    this._worktree = updated;
                    this._worktrees = repositoryTrees;
                    this._branches = branches;
                    this.CreateWorktreeMenuItem.IsEnabled = !updated.IsPrunable && !updated.IsBare;
                    this._pendingDetailRefresh = false;
                }
                else if (this.BranchPicker.IsDropDownOpen || this.BranchPicker.IsKeyboardFocusWithin)
                {
                    return;
                }

                if (this.InlineGitHost.Visibility != Visibility.Visible)
                {
                    var filesOffset = ViewState.ScrollOffset(this.FilesList);
                    var selectedFile = (this.FilesList.SelectedItem as ChangedFile)?.Path;
                    var pendingBranch = (this.BranchPicker.SelectedItem as BranchInfo)?.Name;
                    this._branches = branches;
                    this.ApplyWorktreeState(updated, states.GetValueOrDefault(Path.GetFullPath(updated.Path)));
                    this.FilesList.SelectedItem = this.FilesList.Items.OfType<ChangedFile>().FirstOrDefault(file => file.Path == selectedFile);

                    if (updated.Branch == current.Branch && pendingBranch is not null)
                    {
                        this.BranchPicker.SelectedItem = this.BranchPicker.Items.OfType<BranchInfo>().FirstOrDefault(branch => branch.Name == pendingBranch)
                            ?? this.BranchPicker.SelectedItem;
                    }

                    ViewState.RestoreScroll(this.FilesList, filesOffset);
                }
            }
            if (workspace is not null && (bundleChanged || structureChanged))
            {
                var selected = this.BundleFilesList.SelectedItem as BundleChangedFile;
                var offset = ViewState.ScrollOffset(this.BundleFilesList);
                var rows = MainWindow.CreateOverviewRows(workspace, this._copyStates);
                this.ApplyBundleRows(rows);
                this.BundleFilesList.SelectedItem = this.BundleFilesList.Items.OfType<BundleChangedFile>().FirstOrDefault(file =>
                    file.WorktreePath == selected?.WorktreePath && file.File.Path == selected?.File.Path);
                ViewState.RestoreScroll(this.BundleFilesList, offset);
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!this._isClosed && !this._isBusy && version == this._stateVersion)
            {
                this.StatusText.Text = L10n.T("Не удалось обновить состояние");
                this.StatusText.ToolTip = exception.Message;
            }
        }
        finally
        {
            this._isRefreshing = false;
        }
    }

    private static string TreeSignature(IReadOnlyDictionary<string, IReadOnlyList<WorktreeInfo>> registered) =>
        string.Join('\0', registered.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => pair.Value.Select(tree => $"{pair.Key}|{tree.Path}|{tree.Head}|{tree.Branch}|{tree.IsPrunable}|{tree.IsLocked}")));

    private static IReadOnlyList<WorkspaceRepositoryRow> CreateOverviewRows(ProjectWorkspace workspace, IReadOnlyDictionary<string, WorktreeState> states)
    {
        var rows = new List<WorkspaceRepositoryRow>();

        foreach (var source in workspace.RepositoryPaths.Concat(workspace.WorktreePaths.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = workspace.WorktreePaths.FirstOrDefault(pair => GitService.PathsEqual(pair.Key, source)).Value;
            var state = path is null ? null : states.GetValueOrDefault(Path.GetFullPath(path)) ?? MainWindow.MissingCopy(path);
            rows.Add(new WorkspaceRepositoryRow
            {
                RepositoryPath = source,
                Name = Path.GetFileName(source.TrimEnd('\\', '/')),
                Path = path ?? "Копия не создана",
                Worktree = state?.Worktree,
                ChangedFiles = state?.Files ?? [],
                ChangeCount = state?.ChangeCount ?? 0,
                Error = state?.Error ?? (state is null ? "Копия недоступна" : null)
            });
        }

        return rows;
    }

    private static WorktreeState MissingCopy(string path) => new(new WorktreeInfo(path, "", "", false, false, false, true, "Копия не зарегистрирована"), [], "Копия не зарегистрирована");
}
