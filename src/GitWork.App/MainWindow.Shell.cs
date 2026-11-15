using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class MainWindow
{
    private GitClientView? _gitClient;
    private GitClientView? _inlineGitClient;
    private bool _changingSection;

    private GitClientView? ActiveGitClient => this.GitClientHost.Visibility == Visibility.Visible
        ? this._gitClient
        : this.InlineGitHost.Visibility == Visibility.Visible ? this._inlineGitClient : null;

    private bool CanLeaveGitClients => this._gitClient?.CanLeave != false && this._inlineGitClient?.CanLeave != false;

    private async Task OpenWorktreeInlineAsync(RepositoryInfo repository, WorktreeInfo tree, string mode = "Changes")
    {
        if (!this.CanLeaveGitClients)
        {
            return;
        }

        this.ApplyMainSection("Worktrees");
        this.RepositoryContent.Visibility = Visibility.Collapsed;
        this.WorktreeDetailBorder.Visibility = Visibility.Collapsed;
        this.BundleOverview.Visibility = Visibility.Collapsed;
        this.InlineGitHost.Visibility = Visibility.Visible;
        this.EnsureSidebarFits();
        this.WorktreeActionsColumn.Width = new GridLength(0);
        this._activeWorkspace = null;
        this._repository = repository;
        this._worktree = tree;
        this._worktrees = this._registeredTrees.GetValueOrDefault(repository.Path) ?? [tree];
        this._branches = await this._git.GetBranchesAsync(tree.Path, this._worktrees);
        this.CreateWorktreeMenuItem.IsEnabled = !tree.IsPrunable && !tree.IsBare;

        if (this._inlineGitClient is null)
        {
            this._inlineGitClient = new GitClientView(this._git, this._settings)
            {
                EmbeddedInWorkspace = true
            };
            this._inlineGitClient.ContextChanged += async (selectedRepository, selectedTree) =>
            {
                if (this.InlineGitHost.Visibility != Visibility.Visible)
                {
                    return;
                }

                this._repository = selectedRepository;
                this._worktree = selectedTree;
                this._activeWorkspace = null;
                this._worktrees = this._registeredTrees.GetValueOrDefault(selectedRepository.Path) ?? [selectedTree];
                var stateVersion = this._stateVersion;
                try
                {
                    var branches = await this._git.GetBranchesAsync(selectedTree.Path, this._worktrees);
                    if (stateVersion != this._stateVersion || this.InlineGitHost.Visibility != Visibility.Visible
                        || this._worktree is not { } currentTree || !GitService.PathsEqual(currentTree.Path, selectedTree.Path))
                    {
                        return;
                    }

                    this._branches = branches;
                    this.CreateWorktreeMenuItem.IsEnabled = !selectedTree.IsPrunable && !selectedTree.IsBare;
                }
                catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
                {
                    if (stateVersion != this._stateVersion || this.InlineGitHost.Visibility != Visibility.Visible
                        || this._worktree is not { } currentTree || !GitService.PathsEqual(currentTree.Path, selectedTree.Path))
                    {
                        return;
                    }

                    this.StatusText.Text = L10n.T("Не удалось обновить список веток");
                    this.StatusText.ToolTip = exception.Message;
                    this.CreateWorktreeMenuItem.IsEnabled = !selectedTree.IsPrunable && !selectedTree.IsBare;
                }
            };
            this.InlineGitHost.Content = this._inlineGitClient;
        }

        await this._inlineGitClient.OpenCopyAsync(repository, tree.Path, mode, workspaceContext: true);
    }

    private async Task UpdateGitClientAsync()
    {
        if (this._gitClient is null)
        {
            this._gitClient = new GitClientView(this._git, this._settings);
            this._gitClient.AddRepositoryRequested += (_, _) => this.AddRepository_Click(this, new RoutedEventArgs());
            this._gitClient.BackToWorkspaceRequested += async (_, _) =>
            {
                if (this._gitClient.CanLeave)
                {
                    this.ApplyMainSection("Worktrees");
                    await this.ExecuteAsync(this.ScanAsync, "Обновление worktree…");
                }
            };
            this._gitClient.RemoveRepositoryRequested += path =>
            {
                this._settings.GitRepositoryPaths?.RemoveAll(candidate => GitService.PathsEqual(candidate, path));
                this.SaveSettings();
            };
            this._gitClient.ContextChanged += (repository, tree) =>
            {
                if (this.GitClientHost.Visibility != Visibility.Visible)
                {
                    return;
                }

                this._repository = repository;
                this._worktree = tree;
                this._activeWorkspace = null;
                this.CreateWorktreeMenuItem.IsEnabled = true;
                if (!this._gitClient.IsWorkspaceContext)
                {
                    this._settings.SelectedRepository = repository.Path;
                }
                this.SaveSettings();
            };
            this.GitClientHost.Content = this._gitClient;
        }

        this.ApplyMainSection(this._settings.MainSection);
        var registered = (this._settings.GitRepositoryPaths ?? []).Where(Path.IsPathFullyQualified)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new RepositoryInfo(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), path, Path.GetFileName(Path.GetDirectoryName(path)) ?? ""))
            .OrderBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        await this._gitClient.SetRepositoriesAsync(registered);
        this.StatusText.Text = L10n.T("Готово");
    }

    private void ApplyMainSection(string section, bool preserveContent = false)
    {
        this._changingSection = true;
        var repositories = section != "Worktrees";
        this.RepositoriesSection.IsChecked = repositories;
        this.WorktreesSection.IsChecked = !repositories;
        if (!preserveContent)
        {
            this.GitClientHost.Visibility = repositories ? Visibility.Visible : Visibility.Collapsed;
            this.Workspace.Visibility = repositories ? Visibility.Collapsed : Visibility.Visible;
        }
        this._settings.MainSection = repositories ? "Repositories" : "Worktrees";
        this._changingSection = false;
        if (repositories)
        {
            this.InlineGitHost.Visibility = Visibility.Collapsed;
            this.WorktreeActionsColumn.Width = new GridLength(0);
        }
    }

    private async void MainSection_Changed(object sender, RoutedEventArgs e)
    {
        if (this._changingSection || !this.IsLoaded || sender is not RadioButton { Tag: string section })
        {
            return;
        }

        if (this._isBusy || !this.CanLeaveGitClients)
        {
            this.ApplyMainSection(this._settings.MainSection, preserveContent: true);
            return;
        }

        this.ApplyMainSection(section);
        this.SaveSettings();
        await this.ExecuteAsync(() => section == "Worktrees" ? this.ScanAsync() : this.UpdateGitClientAsync(), "Обновление списка…");
    }

    private async Task OpenCopyInClientAsync(RepositoryInfo repository, string path, string mode = "History")
    {
        if (!this.CanLeaveGitClients)
        {
            return;
        }

        this.InlineGitHost.Visibility = Visibility.Collapsed;
        this.WorktreeActionsColumn.Width = new GridLength(0);
        this.ApplyMainSection("Worktrees");
        if (this._gitClient is null)
        {
            await this.UpdateGitClientAsync();
        }

        this.Workspace.Visibility = Visibility.Collapsed;
        this.GitClientHost.Visibility = Visibility.Visible;
        await this._gitClient!.OpenCopyAsync(repository, path, mode, workspaceContext: true);
        this.SaveSettings();
    }

    private async Task OpenBundleCopyAsync(WorkspaceRepositoryRow row, string mode = "History")
    {
        var repository = this._repositories.FirstOrDefault(item => GitService.PathsEqual(item.Path, row.RepositoryPath))
            ?? new RepositoryInfo(row.Name, row.RepositoryPath, "");
        if (row.Worktree is not { } tree)
        {
            throw new GitException("Рабочая копия недоступна.");
        }

        await this.OpenWorktreeInlineAsync(repository, tree, mode);
        var node = NavigationBuilder.Flatten(this._navigation).FirstOrDefault(item => item.Worktree is { } copy
            && GitService.PathsEqual(copy.Path, row.Path));
        if (node is not null)
        {
            this._isUpdating = true;
            this.SelectNavigationNode(node);
            this._isUpdating = false;
            this._settings.SelectedNavigationKey = node.Key;
            this._displayedNavigationKey = node.Key;
        }

        this.SaveSettings();
    }

    private async void OpenBundleRowGit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceRepositoryRow { IsAvailable: true } row })
        {
            await this.ExecuteAsync(() => this.OpenBundleCopyAsync(row), "Чтение истории…");
        }
    }

    private async void OpenBundleRowActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceRepositoryRow { IsAvailable: true } row }
            || this._activeWorkspace is null || this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        if (this.WorktreeActionsColumn.Width.Value > 0 && this._worktree is { } selected
            && GitService.PathsEqual(selected.Path, row.Path))
        {
            this.CloseWorktreeActions_Click(sender, e);
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var trees = await this._git.GetWorktreesAsync(row.RepositoryPath);
            var tree = trees.FirstOrDefault(copy => GitService.PathsEqual(copy.Path, row.Path) && !copy.IsBare && !copy.IsPrunable)
                ?? throw new GitException("Рабочая копия больше недоступна. Обновите комплект.");
            var state = await WorktreeStateReader.ReadAsync(tree, this._git);
            if (!state.IsAvailable)
            {
                throw new GitException(state.Error ?? "Не удалось прочитать рабочую копию.");
            }

            this._repository = this._repositories.FirstOrDefault(item => GitService.PathsEqual(item.Path, row.RepositoryPath))
                ?? new RepositoryInfo(row.Name, row.RepositoryPath, "");
            this._worktrees = trees;
            this._branches = await this._git.GetBranchesAsync(row.Path, trees);
            this._copyStates[Path.GetFullPath(row.Path)] = state;
            this.ApplyWorktreeState(tree, state);
            this.CreateWorktreeMenuItem.IsEnabled = true;
            this.ActionTargetName.Text = row.Name;
            this.ActionTargetPath.Text = row.Path;
            this.WorktreeDetailBorder.Visibility = Visibility.Visible;
            this.WorktreeActionsColumn.Width = new GridLength(300);
            this.EnsureSidebarFits();
        }, L10n.T("Чтение действий репозитория…"));
    }

    private void ToggleWorktreeActions_Click(object sender, RoutedEventArgs e)
    {
        if (this.WorktreeActionsColumn.Width.Value > 0)
        {
            this.CloseWorktreeActions_Click(sender, e);
            return;
        }

        this.ActionTargetName.Text = this._repository?.Name ?? L10n.T("Действия");
        this.ActionTargetPath.Text = this._worktree?.Path ?? "";
        this.WorktreeDetailBorder.Visibility = Visibility.Visible;
        this.WorktreeActionsColumn.Width = new GridLength(300);
        this.EnsureSidebarFits();
    }

    private void CloseWorktreeActions_Click(object sender, RoutedEventArgs e)
    {
        this._stateVersion++;
        this._pendingDetailRefresh = false;
        this.WorktreeActionsColumn.Width = new GridLength(0);
        this.WorktreeDetailBorder.Visibility = Visibility.Collapsed;
        if (this._activeWorkspace is not null)
        {
            this._worktree = null;
            this._repository = null;
            this.CreateWorktreeMenuItem.IsEnabled = false;
        }
    }
}
