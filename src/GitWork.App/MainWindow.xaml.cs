using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using Microsoft.Win32;
using GitWork.Core;

namespace GitWork.App;

public partial class MainWindow : Window
{
    private readonly GitService _git = new();
    private AppSettings _settings = new();
    private IReadOnlyList<RepositoryInfo> _repositories = [];
    private IReadOnlyList<NavigationNode> _navigation = [];
    private IReadOnlyDictionary<string, IReadOnlyList<WorktreeInfo>> _registeredTrees = new Dictionary<string, IReadOnlyList<WorktreeInfo>>();
    private IReadOnlyList<WorktreeInfo> _worktrees = [];
    private IReadOnlyList<BranchInfo> _branches = [];
    private RepositoryInfo? _repository;
    private WorktreeInfo? _worktree;
    private ProjectWorkspace? _activeWorkspace;
    private string? _displayedNavigationKey;
    private bool _isBusy;
    private bool _isUpdating;

    public MainWindow()
    {
        this.InitializeComponent();
        this.Loaded += (_, _) => this.UpdateLanguageMenu();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.PreviewKeyDown += this.Window_PreviewKeyDown;
        this.SizeChanged += (_, _) => this.EnsureSidebarFits();
        this.Closing += (_, args) => args.Cancel = this._isBusy || !this.CanLeaveGitClients;
        this.InitializeRefresh();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await this.ExecuteAsync(async () =>
        {
            try
            {
                this._settings = AppSettings.Load();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                MessageBox.Show(this, L10n.T("Не удалось прочитать настройки.\n{0}", L10n.T(exception.Message)), L10n.T("Настройки"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            this._settings.GitRepositoryPaths ??= this._settings.AdditionalRepositories.ToList();
            this._isUpdating = true;
            ThemeManager.Apply(this._settings.Theme);
            this.UpdateThemePicker();
            this.NavigationModePicker.SelectedValue = this._settings.NavigationMode;
            this._isUpdating = false;
            this.SidebarColumn.Width = new GridLength(Math.Clamp(double.IsFinite(this._settings.SidebarWidth) ? this._settings.SidebarWidth : 310, 210, 640));
            if (this._settings.MainSection == "Worktrees")
            {
                await this.ScanAsync();
            }
            else
            {
                await this.UpdateGitClientAsync();
            }
        }, L10n.T("Поиск рабочих копий…"));
        this.ConfigureRefresh();
        this.InitializeUpdateChecks(this, e);
    }

    private async Task ExecuteAsync(Func<Task> operation, string message)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        this._isBusy = true;
        this._stateVersion++;
        this.Workspace.IsEnabled = false;
        this.BusyProgress.Visibility = Visibility.Visible;
        this.StatusText.Text = L10n.T(message);
        this.OperationMessage.Text = L10n.T(message);
        this.OperationOverlay.Visibility = Visibility.Visible;
        this.GitClientHost.IsEnabled = false;
        this.InlineGitHost.IsEnabled = false;

        try
        {
            await operation();
            if (this.StatusText.Text == L10n.T(message))
            {
                this.StatusText.Text = L10n.T("Готово");
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            this.StatusText.Text = L10n.T("Операция не завершена.");
            MessageBox.Show(this, L10n.T(exception.Message), "Git Work", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            this._isBusy = false;
            this.Workspace.IsEnabled = true;
            this.GitClientHost.IsEnabled = true;
            this.InlineGitHost.IsEnabled = true;
            this.OperationOverlay.Visibility = Visibility.Collapsed;
            this.BusyProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ScanAsync()
    {
        var roots = this._settings.RootPaths.Where(Directory.Exists).ToArray();
        var candidates = (await Task.Run(() => roots.SelectMany(root => RepositoryScanner.Scan(root)).ToArray())).ToList();
        var additional = this._settings.AdditionalRepositories.Concat(this._settings.Workspaces.SelectMany(workspace => workspace.WorktreePaths.Keys));

        foreach (var path in additional.Where(Directory.Exists))
        {
            if (!candidates.Any(repository => GitService.PathsEqual(repository.Path, path)))
            {
                candidates.Add(new RepositoryInfo(Path.GetFileName(path), path, ""));
            }
        }

        var catalog = await RepositoryCatalog.ResolveAsync(candidates, this._git);
        this._repositories = catalog.Repositories;
        var cache = new ConcurrentDictionary<string, IReadOnlyList<WorktreeInfo>>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<string>(catalog.Errors);

        await Parallel.ForEachAsync(this._repositories, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (repository, _) =>
        {
            try
            {
                cache[repository.Path] = await this._git.GetWorktreesAsync(repository.Path);
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{repository.Path}: {exception.Message}");
            }
        });

        this._registeredTrees = cache;
        this._copyStates = await this.ReadStatesAsync(cache.Values.SelectMany(trees => trees));
        this._lastFullRefresh = DateTime.UtcNow;
        this.ApplyNavigationFilter();
        var nodes = NavigationBuilder.Flatten(this._navigation).ToArray();
        var selected = nodes.FirstOrDefault(node => node.Key == this._settings.SelectedNavigationKey);

        if (selected is null && this._settings.SelectedNavigationKey?.StartsWith("repository:", StringComparison.Ordinal) == true)
        {
            var oldRepository = this._settings.SelectedNavigationKey["repository:".Length..];
            selected = nodes.FirstOrDefault(node => node.Key.StartsWith("worktree:loose:", StringComparison.Ordinal) && node.Worktree is not null && node.Repository is { } repository
                && GitService.PathsEqual(repository.Path, oldRepository));
        }

        if (selected is null)
        {
            selected = nodes.FirstOrDefault(node => this._settings.SelectedWorkspaceId is not null && node.Workspace?.Id == this._settings.SelectedWorkspaceId)
                ?? nodes.FirstOrDefault(node => node.Kind == "Workspace")
                ?? nodes.FirstOrDefault(node => node.Kind == "Worktree" && node.Repository?.Path == this._settings.SelectedRepository && node.Worktree?.IsMain == true)
                ?? nodes.FirstOrDefault(node => node.Kind == "Worktree");
        }

        if (selected is not null)
        {
            this.SelectNavigationNode(selected);
            await this.LoadNavigationNodeAsync(selected);
        }
        else
        {
            this.ClearNavigationDetails(L10n.T("Выбранная копия не найдена или скрыта фильтром"), L10n.T("Выберите элемент слева или измените поиск."));
        }

        this.NavigationCount.Text = L10n.T("Worktree: {0} · комплектов: {1}", cache.Values.Sum(trees => trees.Count(tree => !tree.IsMain && !tree.IsBare)), this._settings.Workspaces.Count);
        this.StatusText.ToolTip = errors.Count == 0 ? null : string.Join("\n\n", errors);
        await this.UpdateGitClientAsync();
    }

    private void ApplyNavigationFilter()
    {
        var expanded = NavigationBuilder.Flatten(this._navigation).Where(node => node.IsExpanded).Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        this._navigation = NavigationBuilder.Build(this._repositories, this._registeredTrees, this._settings.Workspaces,
            this.RepositorySearch.Text, this.NavigationModePicker.SelectedValue?.ToString() ?? "All",
            this._settings.FavoriteWorkspaceIds, this._settings.RecentWorkspaceIds, linkedOnly: true);

        foreach (var node in NavigationBuilder.Flatten(this._navigation))
        {
            if (expanded.Contains(node.Key))
            {
                node.IsExpanded = true;
            }
        }

        this._isUpdating = true;
        this.RepositoryTree.ItemsSource = this._navigation;
        var selected = NavigationBuilder.Flatten(this._navigation).FirstOrDefault(node => node.Key == this._displayedNavigationKey);

        if (selected is not null)
        {
            this.SelectNavigationNode(selected);
        }
        else
        {
            this.ClearNavigationDetails(L10n.T("Выберите комплект или рабочую копию"), L10n.T("Нажмите на элемент в левом списке."));
        }

        this.UpdateNavigationStates();
        this._isUpdating = false;
    }

    private void SelectNavigationNode(NavigationNode selected)
    {
        foreach (var node in NavigationBuilder.Flatten(this._navigation))
        {
            node.IsSelected = node.Key == selected.Key;

            if (node.Children.Count > 0 && NavigationBuilder.Flatten(node.Children).Any(child => child.Key == selected.Key))
            {
                node.IsExpanded = true;
            }
        }
    }

    private void ClearNavigationDetails(string title, string hint)
    {
        this._stateVersion++;
        this.InlineGitHost.Visibility = Visibility.Collapsed;
        this.WorktreeActionsColumn.Width = new GridLength(0);
        this._displayedNavigationKey = null;
        this._activeWorkspace = null;
        this._worktree = null;
        this._repository = null;
        this.RepositoryContent.Visibility = Visibility.Visible;
        this.WorktreeDetailBorder.Visibility = Visibility.Visible;
        this.BundleOverview.Visibility = Visibility.Collapsed;
        this.DetailPanel.IsEnabled = false;
        this.CreateWorktreeMenuItem.IsEnabled = false;
        this.NewBranchButton.IsEnabled = false;
        this.FilesList.ItemsSource = null;
        this.BranchPicker.ItemsSource = null;
        this.RepositoryTitle.Text = title;
        this.RepositoryPath.Text = "";
        this.EmptyTitle.Text = L10n.T("Ничего не выбрано");
        this.EmptyTitle.Visibility = Visibility.Collapsed;
        this.EmptyHint.Text = L10n.T(hint);
        this.EmptyState.Visibility = Visibility.Visible;
        this.ChangesLabel.Text = "";
        this.BranchHint.Text = "";
        this.CleanLabel.Visibility = Visibility.Collapsed;
        this.DiffButton.IsEnabled = false;
        this.DiffButton.Visibility = Visibility.Collapsed;
    }

    private void RepositorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (this.RepositoryTree is not null)
        {
            this.ApplyNavigationFilter();
        }
    }

    private void NavigationModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isUpdating || this.RepositoryTree is null)
        {
            return;
        }

        this._settings.NavigationMode = this.NavigationModePicker.SelectedValue?.ToString() ?? "All";
        this.ApplyNavigationFilter();
        this.SaveSettings();
    }

    private async void RepositoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (this._isUpdating || e.NewValue is not NavigationNode node)
        {
            return;
        }

        if (this._isBusy || !this.CanLeaveGitClients)
        {
            var previous = NavigationBuilder.Flatten(this._navigation).FirstOrDefault(item => item.Key == this._displayedNavigationKey);
            if (previous is not null)
            {
                this._isUpdating = true;
                this.SelectNavigationNode(previous);
                this._isUpdating = false;
            }

            return;
        }

        await this.ExecuteAsync(() => this.LoadNavigationNodeAsync(node), L10n.T("Чтение состояния…"));
    }

    private async Task LoadNavigationNodeAsync(NavigationNode node)
    {
        this._settings.SelectedNavigationKey = node.Key;
        this._displayedNavigationKey = node.Key;

        if (node.Workspace is not null)
        {
            this._settings.RecentWorkspaceIds.Remove(node.Workspace.Id);
            this._settings.RecentWorkspaceIds.Insert(0, node.Workspace.Id);
            this._settings.RecentWorkspaceIds = this._settings.RecentWorkspaceIds.Take(8).ToList();
            await this.LoadBundleAsync(node.Workspace);
        }
        else if (node.Repository is not null)
        {
            node.IsExpanded = true;
            if (node.Worktree is { IsBare: false, IsPrunable: false } tree)
            {
                await this.OpenWorktreeInlineAsync(node.Repository, tree);
            }
            else if (node.Worktree is null)
            {
                var trees = this._registeredTrees.GetValueOrDefault(node.Repository.Path) ?? [];
                var targetTree = node.Children.Select(child => child.Worktree).FirstOrDefault(candidate => candidate is { IsBare: false, IsPrunable: false })
                    ?? trees.FirstOrDefault(candidate => !candidate.IsMain && !candidate.IsBare && !candidate.IsPrunable);
                if (targetTree is not null)
                {
                    await this.OpenWorktreeInlineAsync(node.Repository, targetTree);
                    var child = node.Children.FirstOrDefault(candidate => candidate.Worktree is { } copy && GitService.PathsEqual(copy.Path, targetTree.Path));
                    if (child is not null)
                    {
                        this._isUpdating = true;
                        this.SelectNavigationNode(child);
                        this._isUpdating = false;
                        this._settings.SelectedNavigationKey = child.Key;
                        this._displayedNavigationKey = child.Key;
                    }
                }
                else
                {
                    await this.LoadRepositoryAsync(node.Repository);
                }
            }
            else
            {
                this.InlineGitHost.Visibility = Visibility.Collapsed;
                await this.LoadRepositoryAsync(node.Repository, node.Worktree?.Path);
            }
        }
        else
        {
            this.ClearNavigationDetails(node.Title, L10n.T("Выберите конкретный комплект или рабочую копию в этом разделе."));
        }

        this.SaveSettings();
    }

    private async Task LoadBundleAsync(ProjectWorkspace workspace)
    {
        this.InlineGitHost.Visibility = Visibility.Collapsed;
        this.WorktreeActionsColumn.Width = new GridLength(0);
        this._activeWorkspace = workspace;
        this._worktree = null;
        this.DetailPanel.IsEnabled = false;
        this.CreateWorktreeMenuItem.IsEnabled = false;
        this.NewBranchButton.IsEnabled = false;
        this.RepositoryContent.Visibility = Visibility.Collapsed;
        this.WorktreeDetailBorder.Visibility = Visibility.Collapsed;
        this.BundleOverview.Visibility = Visibility.Visible;
        this.BundleTitle.Text = workspace.DisplayName;
        this.FavoriteButton.Content = this._settings.FavoriteWorkspaceIds.Contains(workspace.Id) ? "★" : "☆";
        this.FavoriteButton.ToolTip = L10n.T(this._settings.FavoriteWorkspaceIds.Contains(workspace.Id) ? "Убрать из избранного" : "В избранное");
        this.BundlePath.Text = workspace.RootPath ?? L10n.T("Папки расположены в разных местах");
        var rows = await WorkspaceStateReader.ReadAsync(workspace, this._git);
        this.ApplyBundleRows(rows);
        this._settings.SelectedWorkspaceId = workspace.Id;
        this.StatusText.Text = L10n.T("Готово");
    }

    private void ApplyBundleRows(IReadOnlyList<WorkspaceRepositoryRow> rows)
    {
        foreach (var row in rows.Where(row => Path.IsPathFullyQualified(row.Path)))
        {
            this._copyStates[Path.GetFullPath(row.Path)] = row.Worktree is not null
                ? new WorktreeState(row.Worktree, row.ChangedFiles, row.Error)
                : MainWindow.MissingCopy(row.Path);
        }

        this.BundleRepositoryRows.ItemsSource = rows;
        var files = rows.SelectMany(row => row.ChangedFiles.Select(file => new BundleChangedFile(row.Name, row.Path, file))).ToArray();
        var view = CollectionViewSource.GetDefaultView(files);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BundleChangedFile.RepositoryName)));
        this.BundleFilesList.ItemsSource = view;
        this.BundleCleanLabel.Visibility = files.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.BundleCleanLabel.Text = L10n.T(rows.Any(row => !row.IsAvailable) ? "Во всех доступных копиях нет изменений" : "Нет изменений");
        this.BundleChangesLabel.Text = files.Length == 0 ? L10n.T("Изменения") : L10n.T("Изменения ({0})", files.Length);
        this.BundleChangesButton.Visibility = files.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var conflicts = rows.Sum(row => row.ConflictCount);
        var missing = rows.Count(row => !row.IsAvailable);
        this.BundleSummary.Text = L10n.T("Репозитории: {0}", rows.Count)
            + (conflicts > 0 ? L10n.T(" · конфликты: {0}", conflicts) : "")
            + (missing > 0 ? L10n.T(" · недоступно: {0}", missing) : "");
        this.BundleSummary.SetResourceReference(TextBlock.ForegroundProperty, conflicts + missing > 0 ? "WarningBrush" : "AccentBrush");
        this.UpdateNavigationStates();
    }

    private async Task LoadRepositoryAsync(RepositoryInfo repository, string? preferredWorktree = null)
    {
        this.InlineGitHost.Visibility = Visibility.Collapsed;
        this.WorktreeActionsColumn.Width = new GridLength(0);
        this._activeWorkspace = null;
        this._repository = repository;
        this._worktree = null;
        this.NewBranchButton.IsEnabled = false;
        this.RepositoryContent.Visibility = Visibility.Visible;
        this.WorktreeDetailBorder.Visibility = Visibility.Visible;
        this.BundleOverview.Visibility = Visibility.Collapsed;
        this.DetailPanel.IsEnabled = false;
        this.FilesList.ItemsSource = null;
        this.DiffButton.IsEnabled = false;
        this.DiffButton.Visibility = Visibility.Collapsed;
        this.CleanLabel.Visibility = Visibility.Collapsed;
        this.ChangesLabel.Text = "";
        this.BranchPicker.ItemsSource = null;
        this.RepositoryTitle.Text = repository.Name;
        this.RepositoryPath.Text = preferredWorktree ?? repository.Path;
        this._worktrees = await this._git.GetWorktreesAsync(repository.Path);
        this._branches = await this._git.GetBranchesAsync(repository.Path, this._worktrees);
        var visible = this._worktrees.Where(tree => preferredWorktree is null
            ? !this.IsWorkspaceCopy(tree.Path)
            : GitService.PathsEqual(tree.Path, preferredWorktree)).ToArray();

        var selected = visible.FirstOrDefault(tree => !tree.IsBare && !tree.IsPrunable) ?? visible.FirstOrDefault();
        this.EmptyState.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        this.RepositoryTitle.Text = repository.Name;
        this.RepositoryPath.Text = preferredWorktree ?? repository.Path;
        this.CreateWorktreeMenuItem.IsEnabled = true;
        this._settings.SelectedRepository = repository.Path;

        if (selected is not null)
        {
            await this.LoadWorktreeAsync(selected);
        }
        else
        {
            this.FilesList.ItemsSource = null;
            this.BranchPicker.ItemsSource = null;
            this.EmptyTitle.Text = L10n.T("Копия не найдена");
            this.EmptyTitle.Visibility = Visibility.Visible;
            this.EmptyHint.Text = L10n.T("Обновите список рабочих копий");
            this.StatusText.Text = L10n.T("Копия недоступна");
        }
    }

    private async void ConfigureBundle_Click(object sender, RoutedEventArgs e)
    {
        if (this._activeWorkspace is null || this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        new WorkspaceManagerWindow(this._activeWorkspace, this._settings, this._git) { Owner = this }.ShowDialog();

        if (!this._settings.Workspaces.Contains(this._activeWorkspace))
        {
            this._settings.SelectedNavigationKey = null;
        }

        await this.ExecuteAsync(this.ScanAsync, "Обновление рабочих копий…");
    }

    private async void CreateBundle_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        if (this._repositories.Count == 0)
        {
            await this.ExecuteAsync(this.ScanAsync, "Поиск исходных репозиториев…");
        }

        var suggested = this._activeWorkspace?.RepositoryPaths
            ?? this._settings.Projects.FirstOrDefault(project => this._repository is not null && project.RepositoryPaths.Any(path => GitService.PathsEqual(path, this._repository.Path)))?.RepositoryPaths
            ?? (this._repository is null ? [] : new List<string> { this._repository.Path });
        var picker = new SelectWorkspaceRepositoriesWindow(this._repositories, suggested) { Owner = this };

        if (picker.ShowDialog() != true || picker.PrimaryRepository is null)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var choices = new List<ProjectWorktreeChoice>();

            foreach (var repository in picker.SelectedRepositories)
            {
                var trees = await this._git.GetWorktreesAsync(repository.Path);
                var branches = (await this._git.GetBranchesAsync(repository.Path, trees)).Select(branch => branch with { OccupiedPath = null }).ToArray();
                var source = branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == trees.FirstOrDefault()?.Branch) ?? branches.FirstOrDefault();
                choices.Add(new ProjectWorktreeChoice(repository, branches, source));
            }

            var composition = new ProjectGroup { Name = picker.PrimaryRepository.Name, RepositoryPaths = picker.SelectedRepositories.Select(repository => repository.Path).ToList() };
            var dialog = new CreateProjectWorkspaceWindow(composition, choices) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (this._settings.Workspaces.Any(workspace => string.Equals(workspace.DisplayName, dialog.WorkspaceName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new GitException("Такое название комплекта уже используется. Выберите другое имя.");
            }

            var progress = new Progress<string>(text =>
            {
                this.OperationMessage.Text = text;
                this.StatusText.Text = text;
            });
            var result = await new ProjectWorktreeService(this._git).CreateAsync(dialog.Plans, progress);

            if (result.Completed.Count > 0)
            {
                var workspace = new ProjectWorkspace
                {
                    Name = dialog.WorkspaceName,
                    RepositoryPaths = composition.RepositoryPaths,
                    PrimaryRepositoryPath = picker.PrimaryRepository.Path,
                    WorktreePaths = result.Completed.ToDictionary(plan => plan.RepositoryPath, plan => plan.TargetPath, StringComparer.OrdinalIgnoreCase)
                };
                this._settings.Workspaces.Add(workspace);
                this._settings.MainSection = "Worktrees";
                this._settings.SelectedNavigationKey = "workspace:" + workspace.Id;
                this.RepositorySearch.Text = "";
                this.NavigationModePicker.SelectedValue = "All";
                this.SaveSettings();
                await this.ScanAsync();
            }

            if (result.Failed is not null)
            {
                MessageBox.Show(this, $"Создано копий: {result.Completed.Count}.\nОшибка на шаге {result.Failed.TargetPath}:\n{result.Error}\n\nСозданные копии сохранены в комплекте.", "Комплект создан не полностью", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, L10n.T("Создание комплекта…"));
    }

    private async Task LoadWorktreeAsync(WorktreeInfo tree)
    {
        var state = await WorktreeStateReader.ReadAsync(tree, this._git);
        this._copyStates[Path.GetFullPath(tree.Path)] = state;
        this.ApplyWorktreeState(tree, state);
        this.UpdateNavigationStates();
    }

    private void ApplyWorktreeState(WorktreeInfo tree, WorktreeState? state)
    {
        this._pendingDetailRefresh = false;
        this._worktree = tree;
        this.RepositoryPath.Text = tree.Path;
        this.FilesList.ItemsSource = null;
        this.CleanLabel.Visibility = Visibility.Collapsed;
        var isAvailable = state?.IsAvailable == true;
        this.DetailPanel.IsEnabled = isAvailable;
        this.NewBranchButton.IsEnabled = isAvailable;
        this.EmptyState.Visibility = isAvailable ? Visibility.Collapsed : Visibility.Visible;

        if (!isAvailable)
        {
            this.DiffButton.IsEnabled = false;
            this.DiffButton.Visibility = Visibility.Collapsed;
            this.BranchPicker.ItemsSource = null;
            this.EmptyState.Visibility = Visibility.Visible;
            this.EmptyTitle.Visibility = Visibility.Visible;
            this.EmptyTitle.Text = L10n.T("Копия недоступна");
            this.EmptyHint.Text = state?.Error ?? tree.Reason ?? "Проверьте папку рабочей копии";
            this.StatusText.Text = L10n.T("Копия недоступна");
            return;
        }

        var changes = state!.Files;
        var branches = this._branches.Where(branch => !branch.IsRemote)
            .Select(branch => GitService.PathsEqual(branch.OccupiedPath ?? tree.Path, tree.Path)
                ? branch with { OccupiedPath = null } : branch).ToArray();

        this._isUpdating = true;
        this.BranchPicker.ItemsSource = branches;
        this.BranchPicker.SelectedItem = branches.FirstOrDefault(branch => branch.Name == tree.Branch);
        this._isUpdating = false;
        this.BranchPicker.IsEnabled = changes.Count == 0;
        this.SwitchButton.IsEnabled = changes.Count == 0 && branches.Length > 0;
        this.BranchHint.Text = changes.Count > 0 ? "Есть несохранённые изменения" : "";
        this.BranchHint.Visibility = changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.ChangesLabel.Text = $"({changes.Count})";
        this.ChangesLabel.Visibility = changes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        this.FilesList.ItemsSource = changes;
        this.CleanLabel.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.DiffButton.IsEnabled = changes.Count > 0;
        this.DiffButton.Visibility = changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.StatusText.Text = L10n.T("Готово");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await this.ExecuteAsync(
        () => this.ActiveGitClient is { } client ? client.RefreshAsync() : this.ScanAsync(), "Обновление…");

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            this.Refresh_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (this.ActiveGitClient is { } client)
            {
                e.Handled = client.FocusSearch();
                return;
            }

            this.RepositorySearch.Focus();
            this.RepositorySearch.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && this.RepositorySearch.IsKeyboardFocusWithin)
        {
            this.RepositorySearch.Clear();
            e.Handled = true;
        }
    }

    private async void ChangeRoot_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        var dialog = new ProjectRootsWindow(this._settings.RootPaths) { Owner = this };

        if (dialog.ShowDialog() != true || !this.CanLeaveGitClients)
        {
            return;
        }

        this._settings.RootPaths = dialog.RootPaths;
        this.SaveSettings();
        await this.ExecuteAsync(this.ScanAsync, "Поиск репозиториев…");
    }

    private async void AddRepository_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        var initialDirectory = this._settings.RootPaths.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dialog = new AddRepositoryWindow(this._git, initialDirectory) { Owner = this };

        if (dialog.ShowDialog() != true || dialog.RepositoryPath is not { } path)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var root = await this._git.GetRepositoryRootAsync(path);
            this._settings.GitRepositoryPaths ??= [];
            if (!this._settings.GitRepositoryPaths.Any(path => GitService.PathsEqual(path, root)))
            {
                this._settings.GitRepositoryPaths.Add(root);
            }

            this._settings.SelectedRepository = root;
            this.SaveSettings();
            this.ApplyMainSection("Repositories");
            await this.UpdateGitClientAsync();
            var added = new RepositoryInfo(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), root, Path.GetFileName(Path.GetDirectoryName(root)) ?? "");
            await this._gitClient!.OpenCopyAsync(added, root, workspaceContext: false);
        }, L10n.T("Открытие репозитория…"));
    }

    private async void SwitchBranch_Click(object sender, RoutedEventArgs e)
    {
        if (this._worktree is null || this._repository is null || this.BranchPicker.SelectedItem is not BranchInfo branch)
        {
            return;
        }

        var tree = this._worktree;
        var repository = this._repository;

        if (branch.Name == tree.Branch)
        {
            this.StatusText.Text = L10n.T("Эта ветка уже выбрана.");
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            await this._git.SwitchBranchAsync(tree.Path, branch.Name);
            await this.ScanAsync();
        }, $"Переключение на {branch.Name}…");
    }

    private async void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        if (this._worktree is not { } tree || this._isBusy)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var trees = await this._git.GetWorktreesAsync(tree.Path);
            var branches = await this._git.GetBranchesAsync(tree.Path, trees);
            var dialog = new CreateBranchWindow(tree.Path, branches, tree.Branch) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                this.StatusText.Text = L10n.T("Создание ветки отменено.");
                return;
            }

            if (this._activeWorkspace is null)
            {
                this._settings.SelectedNavigationKey = NavigationBuilder.Flatten(this._navigation)
                    .FirstOrDefault(node => node.Worktree is { } copy && GitService.PathsEqual(copy.Path, tree.Path))?.Key
                    ?? this._settings.SelectedNavigationKey;
            }

            try
            {
                await this._git.CreateBranchAsync(tree.Path, dialog.BranchName, dialog.StartPoint);
            }
            finally
            {
                await this.ScanAsync();
            }
        }, L10n.T("Создание ветки…"));
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this.IsLoaded || this._isUpdating || this.ThemePicker.SelectedValue is not string theme)
        {
            return;
        }

        ThemeManager.Apply(theme);
        this._settings.Theme = ThemeManager.CurrentTheme;
        this.SaveSettings();
    }

    private void UpdateThemePicker()
    {
        this.ThemePicker.SelectedValue = ThemeManager.CurrentTheme;
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy)
        {
            return;
        }

        var dialog = new ToolSettingsWindow(this._settings.Tools) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            this._settings.Tools = dialog.Settings;
            this.SaveSettings();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => this.Close();

    private async void CreateWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (this._repository is null || this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        var repository = this._repository;
        var trees = await this._git.GetWorktreesAsync(repository.Path);
        var branches = await this._git.GetBranchesAsync(repository.Path, trees);
        if (!this.CanLeaveGitClients)
        {
            return;
        }

        var dialog = new CreateWorktreeWindow(repository, branches, this._worktree?.Branch) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            await this._git.CreateWorktreeAsync(repository.Path, dialog.TargetPath, dialog.BranchName, dialog.StartPoint);
            this._settings.SelectedNavigationKey = "worktree:loose:" + Path.GetFullPath(dialog.TargetPath);
            this.RepositorySearch.Text = "";
            this.NavigationModePicker.SelectedValue = "All";
            await this.ScanAsync();
        }, L10n.T("Создание worktree…"));
    }

    private async void RemoveWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (this._repository is null || this._worktree is null || this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        var repository = this._repository;
        var tree = this._worktree;
        if (tree.IsMain || tree.IsBare || tree.IsLocked || tree.IsPrunable)
        {
            return;
        }

        if (this.IsWorkspaceCopy(tree.Path))
        {
            MessageBox.Show(this, "Эта копия входит в комплект. Управляйте удалением через «Настроить комплект».");
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var changes = await this._git.GetStatusAsync(tree.Path, true);
            var message = L10n.T("Удалить рабочую папку?\n\n{0}\n\nВетка {1} останется.", tree.Path, tree.BranchLabel);
            if (changes.Count > 0)
            {
                message += "\n\n" + L10n.T("Незакоммиченные изменения: {0} файлов. Они будут безвозвратно удалены. Продолжить?", changes.Count);
            }

            var answer = MessageBox.Show(this, message, L10n.T("Удаление worktree"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            await this._git.RemoveWorktreeAsync(repository.Path, tree.Path, allowUncommittedChanges: changes.Count > 0);
            this._settings.SelectedNavigationKey = null;
            await this.ScanAsync();
        }, L10n.T("Удаление рабочей копии…"));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => this.OpenSelectedPath("folder");

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) => this.OpenSelectedPath("terminal");

    private void OpenIde_Click(object sender, RoutedEventArgs e) => this.OpenSelectedPath("ide");

    private async void OpenSelectedPath(string action)
    {
        if (this._worktree is not { } tree || this._isBusy)
        {
            return;
        }

        await this.ExecuteAsync(() =>
        {
            var start = action switch
            {
                "terminal" => WorkspaceLauncher.PrepareTerminal(tree.Path, this._settings.Tools),
                "ide" => WorkspaceLauncher.PrepareIde(this, tree.Path, this._settings.Tools),
                _ => new ProcessStartInfo(tree.Path) { UseShellExecute = true }
            };

            if (start is not null)
            {
                Process.Start(start);
                this.StatusText.Text = L10n.T("Открыто");
            }

            return Task.CompletedTask;
        }, L10n.T("Открытие…"));
    }

    private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => this.ShowDiff_Click(sender, e);

    private async void ShowDiff_Click(object sender, RoutedEventArgs e)
    {
        if (this._worktree is null)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var tree = this._worktree;
            var changes = await this._git.GetStatusAsync(tree.Path, true);
            var selectedPath = (this.FilesList.SelectedItem as ChangedFile)?.Path;
            await this.OpenCopyInClientAsync(this._repository ?? new RepositoryInfo(tree.Name, tree.Path, ""), tree.Path, "Changes");
            await this.ScanAsync();
        }, L10n.T("Чтение diff…"));
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (this._activeWorkspace is not { } workspace || this._isBusy)
        {
            return;
        }

        if (!this._settings.FavoriteWorkspaceIds.Remove(workspace.Id))
        {
            this._settings.FavoriteWorkspaceIds.Add(workspace.Id);
        }

        this.FavoriteButton.Content = this._settings.FavoriteWorkspaceIds.Contains(workspace.Id) ? "★" : "☆";
        this.FavoriteButton.ToolTip = this._settings.FavoriteWorkspaceIds.Contains(workspace.Id) ? "Убрать из избранного" : "В избранное";
        this.ApplyNavigationFilter();
        this.SaveSettings();
    }

    private async void ShowBundleChanges_Click(object sender, RoutedEventArgs e)
    {
        if (this._activeWorkspace is not { } workspace)
        {
            return;
        }

        var selected = this.BundleFilesList.SelectedItem as BundleChangedFile;
        await this.ExecuteAsync(async () =>
        {
            var rows = await WorkspaceStateReader.ReadAsync(workspace, this._git);
            var sources = rows.Where(row => row.IsAvailable).Select(row => new ChangeSource(row.Name, row.Path, row.ChangedFiles)).ToArray();

            if (sources.Length == 0)
            {
                throw new GitException("Нет доступных рабочих копий для просмотра изменений.");
            }

            this.ApplyMainSection("Worktrees");
            this.Workspace.Visibility = Visibility.Collapsed;
            this.GitClientHost.Visibility = Visibility.Visible;
            await this._gitClient!.OpenBundleChangesAsync(workspace.DisplayName, sources, selected?.File.Path, selected?.WorktreePath);
        }, L10n.T("Чтение изменений комплекта…"));
    }

    private void BundleFilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => this.ShowBundleChanges_Click(sender, e);

    private async void CompareWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (this._worktree is { } tree && !this._isBusy)
        {
            await this.OpenCopyInClientAsync(this._repository ?? new RepositoryInfo(tree.Name, tree.Path, ""), tree.Path, "Comparison");
        }
    }

    private async void CompareBundleRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceRepositoryRow { IsAvailable: true } row } && !this._isBusy)
        {
            await this.OpenBundleCopyAsync(row, "Comparison");
        }
    }

    private bool IsWorkspaceCopy(string path) => this._settings.Workspaces.Any(workspace =>
        workspace.WorktreePaths.Values.Any(copy => GitService.PathsEqual(copy, path)));

    private void SaveSettings()
    {
        try
        {
            this._settings.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            this.StatusText.Text = $"Настройки не сохранены: {exception.Message}";
        }
    }

    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        this._settings.SidebarWidth = this.SidebarColumn.ActualWidth;
        this.SaveSettings();
    }

    private void EnsureSidebarFits()
    {
        if (this.Workspace is null || this.Workspace.ActualWidth == 0)
        {
            return;
        }

        var minimumContentWidth = this.InlineGitHost.Visibility == Visibility.Visible ? 600
            : this._activeWorkspace is not null && this.WorktreeActionsColumn.Width.Value > 0 ? 500 : 354;
        var actionsWidth = this.WorktreeActionsColumn.Width.IsAbsolute
            ? this.WorktreeActionsColumn.Width.Value : this.WorktreeActionsColumn.ActualWidth;
        var maximum = Math.Max(210, Math.Min(640, this.Workspace.ActualWidth - actionsWidth - minimumContentWidth));

        if (this.SidebarColumn.Width.Value > maximum)
        {
            this.SidebarColumn.Width = new GridLength(maximum);
        }
    }
}
