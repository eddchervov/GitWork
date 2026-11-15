using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView : UserControl
{
    private readonly GitService _git;
    private readonly AppSettings _settings;
    private IReadOnlyList<RepositoryInfo> _repositories = [];
    private RepositoryInfo? _selectedRepository;
    private WorktreeInfo? _selectedCopy;
    private string _mode = "Changes";
    private bool _updating;
    private bool _loading;
    private int _branchVersion;
    private bool _workspaceContext;

    public bool IsWorkspaceContext => this._workspaceContext;

    public bool EmbeddedInWorkspace { get; set; }

    public event EventHandler? BackToWorkspaceRequested;

    public event Action<string>? RemoveRepositoryRequested;

    public event EventHandler? AddRepositoryRequested;

    public event Action<RepositoryInfo, WorktreeInfo>? ContextChanged;

    public bool CanLeave => !this._loading && (this.ViewHost.Content switch
    {
        GitHistoryView history => history.CanLeave,
        ChangesView changes => changes.CanLeave,
        _ => true
    });

    public GitClientView(GitService git, AppSettings settings)
    {
        this._git = git;
        this._settings = settings;
        this.InitializeComponent();
    }

    public async Task SetRepositoriesAsync(IReadOnlyList<RepositoryInfo> repositories, bool resetView = false)
    {
        if (!this.CanLeave)
        {
            return;
        }

        var previous = this._workspaceContext ? this._settings.SelectedRepository : this._selectedRepository?.Path ?? this._settings.SelectedRepository;
        this._updating = true;
        if (resetView || this._workspaceContext)
        {
            this._mode = "Changes";
        }

        this.ChangesTab.IsChecked = this._mode == "Changes";
        this.HistoryTab.IsChecked = this._mode == "History";
        this.ComparisonTab.IsChecked = this._mode == "Comparison";
        this.SetWorkspaceContext(false);
        this._repositories = repositories;
        this.ApplySearch();
        this.RepositoriesList.SelectedItem = this.RepositoriesList.Items.OfType<RepositoryInfo>()
            .FirstOrDefault(repository => !string.IsNullOrWhiteSpace(previous) && GitService.PathsEqual(repository.Path, previous))
            ?? this.RepositoriesList.Items.OfType<RepositoryInfo>().FirstOrDefault();
        this._updating = false;
        await this.SelectRepositoryAsync(this.RepositoriesList.SelectedItem as RepositoryInfo, null);
    }

    public async Task OpenCopyAsync(RepositoryInfo repository, string path, string mode = "History", bool workspaceContext = true)
    {
        if (!this.CanLeave)
        {
            return;
        }

        this.SetWorkspaceContext(workspaceContext);
        this._updating = true;
        this.SearchBox.Clear();
        this.RepositoriesList.SelectedItem = this._repositories.FirstOrDefault(item => GitService.PathsEqual(item.Path, repository.Path));
        this._mode = mode;
        this.ChangesTab.IsChecked = mode == "Changes";
        this.HistoryTab.IsChecked = mode == "History";
        this.ComparisonTab.IsChecked = mode == "Comparison";
        this._updating = false;
        await this.SelectRepositoryAsync(repository, path);
    }

    private void ApplySearch()
    {
        var repositories = this._repositories
            .Where(repository => SearchMatcher.Matches(this.SearchBox.Text, repository.Name, repository.Path))
            .OrderBy(repository => repository.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var view = new ListCollectionView(repositories);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RepositoryInfo.Group)));
        this.RepositoriesList.ItemsSource = view;
    }

    private async void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (this.RepositoriesList is null || this._updating || !this.CanLeave)
        {
            return;
        }

        this._updating = true;
        this.ApplySearch();
        this.RepositoriesList.SelectedItem = this.RepositoriesList.Items.OfType<RepositoryInfo>()
            .FirstOrDefault(repository => repository == this._selectedRepository)
            ?? this.RepositoriesList.Items.OfType<RepositoryInfo>().FirstOrDefault();
        this._updating = false;
        await this.SelectRepositoryAsync(this.RepositoriesList.SelectedItem as RepositoryInfo);
    }

    private async void Repository_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this._updating)
        {
            return;
        }

        if (!this.CanLeave)
        {
            this._updating = true;
            this.RepositoriesList.SelectedItem = this._selectedRepository;
            this._updating = false;
            return;
        }

        await this.SelectRepositoryAsync(this.RepositoriesList.SelectedItem as RepositoryInfo);
    }

    private async Task SelectRepositoryAsync(RepositoryInfo? repository, string? preferredCopy = null)
    {
        await this.RunAsync(async () =>
        {
            if (!this._workspaceContext)
            {
                this.SetWorkspaceContext(false);
            }

            this._selectedRepository = repository;
            this._selectedCopy = null;
            this.CurrentBranchLabel.Text = "";
            this.BranchesPicker.ItemsSource = null;
            this.ActionsButton.IsEnabled = false;
            this.DetachView();
            this.ViewTabs.Visibility = Visibility.Collapsed;
            this.RepositoryName.Text = repository?.Name ?? L10n.T(this._repositories.Count == 0 ? "Добавьте репозиторий" : "Ничего не найдено");
            this.RepositoryLocation.Text = repository?.Path ?? L10n.T("Добавьте папку через кнопку слева или меню «Файл».");
            if (repository is null)
            {
                return;
            }

            var trees = await this._git.GetWorktreesAsync(repository.Path);
            var path = preferredCopy ?? repository.Path;
            this._selectedCopy = trees.FirstOrDefault(tree => !tree.IsBare && !tree.IsPrunable && GitService.PathsEqual(tree.Path, path));
            if (this._selectedCopy is null)
            {
                throw new GitException("Эта папка не является доступной рабочей папкой Git. Проверьте путь или добавьте её заново.");
            }

            await this.ShowCurrentViewAsync();
        });
    }

    private async void View_Changed(object sender, RoutedEventArgs e)
    {
        if (this.ViewHost is null || this._updating || sender is not RadioButton { Tag: string mode })
        {
            return;
        }

        if (!this.CanLeave)
        {
            this._updating = true;
            this.ChangesTab.IsChecked = this._mode == "Changes";
            this.HistoryTab.IsChecked = this._mode == "History";
            this.ComparisonTab.IsChecked = this._mode == "Comparison";
            this._updating = false;
            return;
        }

        this._mode = mode;
        await this.RunAsync(this.ShowCurrentViewAsync);
    }

    private async Task ShowCurrentViewAsync()
    {
        this.ActionsButton.IsEnabled = true;
        this.HistoryTab.IsEnabled = true;
        this.ComparisonTab.IsEnabled = true;
        this.DetachView();
        if (this._selectedCopy is not { } tree || this._selectedRepository is not { } repository)
        {
            return;
        }

        this.RepositoryLocation.Text = tree.Path;
        this.ViewTabs.Visibility = Visibility.Visible;
        this.ContextChanged?.Invoke(repository, tree);
        if (this._mode == "Changes")
        {
            var files = await this._git.GetStatusAsync(tree.Path, true);
            this.ViewHost.Content = new ChangesView(repository.Name, [new ChangeSource(repository.Name, tree.Path, files)], this._git, autoRefresh: this._settings.AutoRefresh);
        }
        else if (this._mode == "Comparison")
        {
            this.ViewHost.Content = new BranchComparisonView(tree.Path, this._git);
        }
        else
        {
            var history = new GitHistoryView([new GraphRepositoryChoice(repository.Name, tree.Path)], this._git);
            history.RepositoryStateChanged += async (_, _) =>
            {
                if (this.ViewHost.Content == history)
                {
                    await this.RefreshBranchesAsync();
                }
            };
            this.ViewActionsHost.Content = history.TakeToolbar();
            this.ViewHost.Content = history;
        }

        await this.RefreshBranchesAsync();
    }

    public async Task OpenBundleChangesAsync(string title, IReadOnlyList<ChangeSource> sources, string? selectedPath, string? selectedWorktree)
    {
        if (!this.CanLeave)
        {
            return;
        }

        await this.RunAsync(() =>
        {
            this.SetWorkspaceContext(true);
            this.ViewTabs.Visibility = Visibility.Visible;
            this.CurrentBranchLabel.Text = "";
            this.DetachView();
            this._updating = true;
            this.RepositoriesList.SelectedItem = null;
            this.ChangesTab.IsChecked = true;
            this.HistoryTab.IsEnabled = false;
            this.ComparisonTab.IsEnabled = false;
            this._mode = "Changes";
            this.RepositoryName.Text = title;
            this.RepositoryLocation.Text = L10n.T("Изменения комплекта · репозиториев: {0}", sources.Count);
            this.ActionsButton.IsEnabled = false;
            this.ActionsPanel.Visibility = Visibility.Collapsed;
            this.ActionsColumn.Width = new GridLength(0);
            this._selectedRepository = null;
            this._selectedCopy = null;
            this.ViewHost.Content = new ChangesView(title, sources, this._git, selectedPath, selectedWorktree, this._settings.AutoRefresh);
            this._updating = false;

            return Task.CompletedTask;
        });
    }

    public async Task RefreshAsync()
    {
        if (!this.CanLeave)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            if (this.ViewHost.Content is GitHistoryView history)
            {
                await history.RefreshAsync();
            }
            else if (this.ViewHost.Content is ChangesView changes)
            {
                await changes.RefreshAsync();
            }
            else if (this._selectedCopy is not null)
            {
                await this.ShowCurrentViewAsync();
            }

            await this.RefreshBranchesAsync();
        });
    }

    public bool FocusSearch()
    {
        var search = (this.ViewHost.Content as UserControl)?.FindName("SearchBox") as TextBox
            ?? (this._workspaceContext ? null : this.SearchBox);
        if (search is null)
        {
            return false;
        }

        search.Focus();
        search.SelectAll();

        return true;
    }

    private void DetachView()
    {
        this._branchVersion++;
        this.ViewActionsHost.Content = null;
        switch (this.ViewHost.Content)
        {
            case GitHistoryView history: history.Detach(); break;
            case ChangesView changes: changes.Detach(); break;
            case BranchComparisonView comparison: comparison.Detach(); break;
        }

        this.ViewHost.Content = null;
    }

    private async Task RefreshBranchesAsync()
    {
        if (this._selectedCopy is not { } tree)
        {
            return;
        }

        var version = ++this._branchVersion;
        try
        {
            var trees = await this._git.GetWorktreesAsync(tree.Path);
            var branches = await this._git.GetBranchesAsync(tree.Path, trees);
            if (version != this._branchVersion || this._selectedCopy?.Path != tree.Path)
            {
                return;
            }

            var actual = trees.FirstOrDefault(copy => GitService.PathsEqual(copy.Path, tree.Path));
            this._selectedCopy = actual ?? tree;
            this.CurrentBranchLabel.Text = this._selectedCopy.BranchLabel;
            this.BranchesPicker.ItemsSource = branches;
            this.BranchesPicker.SelectedItem = branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == this._selectedCopy.Branch);
            if (this._selectedRepository is { } repository)
            {
                this.ContextChanged?.Invoke(repository, this._selectedCopy);
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (version == this._branchVersion)
            {
                this.ErrorText.Text = L10n.T(exception.Message);
            }
        }
    }

    private void SetWorkspaceContext(bool workspace)
    {
        this._workspaceContext = workspace;
        this.RepositorySidebar.Visibility = workspace ? Visibility.Collapsed : Visibility.Visible;
        this.RepositorySplitter.Visibility = workspace ? Visibility.Collapsed : Visibility.Visible;
        this.RepositoryColumn.MinWidth = workspace ? 0 : 180;
        this.RepositoryColumn.Width = new GridLength(workspace ? 0 : 240);
        this.RepositorySplitterColumn.Width = new GridLength(workspace ? 0 : 7);
        this.BackToWorkspace.Visibility = workspace && !this.EmbeddedInWorkspace ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackToWorkspace_Click(object sender, RoutedEventArgs e) => this.BackToWorkspaceRequested?.Invoke(this, EventArgs.Empty);

    private async void RemoveRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || sender is not MenuItem item || ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu { PlacementTarget: ListBoxItem { DataContext: RepositoryInfo repository } })
        {
            return;
        }

        this.RemoveRepositoryRequested?.Invoke(repository.Path);
        await this.SetRepositoriesAsync(this._repositories.Where(candidate => !GitService.PathsEqual(candidate.Path, repository.Path)).ToArray());
    }

    private void Repository_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = !this.CanLeave;
    }

    private void AddRepository_Click(object sender, RoutedEventArgs e) => this.AddRepositoryRequested?.Invoke(this, EventArgs.Empty);

    private void Actions_Click(object sender, RoutedEventArgs e)
    {
        var visible = this.ActionsPanel.Visibility != Visibility.Visible;
        this.ActionsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        this.ActionsColumn.Width = new GridLength(visible && !this.EmbeddedInWorkspace ? 260 : 0);
        if (this.EmbeddedInWorkspace)
        {
            Grid.SetColumn(this.ActionsPanel, 2);
            Grid.SetColumnSpan(this.ActionsPanel, 2);
            this.ActionsPanel.Width = 260;
            this.ActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || this._selectedCopy is not { } tree || this.BranchesPicker.SelectedItem is not BranchInfo branch)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            await this._git.SwitchBranchAsync(tree.Path, branch.Name);
            await this.ShowCurrentViewAsync();
        });
    }

    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || this._selectedCopy is not { } tree)
        {
            return;
        }

        var dialog = new CreateBranchWindow(tree.Path, this.BranchesPicker.Items.OfType<BranchInfo>().ToArray(), tree.Branch) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            await this.RunAsync(async () =>
            {
                await this._git.CreateBranchAsync(tree.Path, dialog.BranchName, dialog.StartPoint);
                await this.ShowCurrentViewAsync();
            });
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || this._selectedCopy is not { } tree || sender is not Button { Tag: string action })
        {
            return;
        }

        await this.RunAsync(() =>
        {
            var start = action switch
            {
                "ide" => WorkspaceLauncher.PrepareIde(Window.GetWindow(this), tree.Path, this._settings.Tools),
                "terminal" => WorkspaceLauncher.PrepareTerminal(tree.Path, this._settings.Tools),
                _ => new ProcessStartInfo(tree.Path) { UseShellExecute = true }
            };

            if (start is not null)
            {
                Process.Start(start);
            }

            return Task.CompletedTask;
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (this._loading)
        {
            return;
        }

        this._loading = true;
        this.ErrorText.Text = L10n.T("Загрузка…");
        try
        {
            await action();
            this.ErrorText.Text = "";
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            this.ErrorText.Text = L10n.T(exception.Message);
        }
        finally
        {
            this._updating = false;
            this._loading = false;
        }
    }
}
