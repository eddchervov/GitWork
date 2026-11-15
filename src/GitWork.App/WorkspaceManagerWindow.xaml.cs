using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class WorkspaceManagerWindow : Window
{
    private readonly string _workspaceId;
    private readonly AppSettings _settings;
    private readonly GitService _git;
    private ProjectWorkspace? _selected;
    private bool _isBusy;

    public WorkspaceManagerWindow(ProjectWorkspace workspace, AppSettings settings, GitService git)
    {
        this._workspaceId = workspace.Id;
        this._settings = settings;
        this._git = git;
        this._selected = workspace;
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Closing += (_, args) => args.Cancel = this._isBusy;
        this.ProjectTitle.Text = L10n.T("Настройки комплекта");
        this.MinWidth = 900;
        this.Width = 1000;
        this.Title = L10n.T("Настройки комплекта · Git Work");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await this.ExecuteAsync(this.ReloadAsync, "Чтение комплектов…");
    }

    private async Task ExecuteAsync(Func<Task> action, string status)
    {
        if (this._isBusy)
        {
            return;
        }

        this._isBusy = true;
        this.ManagerContent.IsEnabled = false;
        this.Progress.Visibility = Visibility.Visible;
        this.StatusText.Text = L10n.T(status);

        try
        {
            await action();
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            this.StatusText.Text = L10n.T("Операция не завершена.");
            MessageBox.Show(this, L10n.T(exception.Message), L10n.T("Настройки комплекта"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            this.ManagerContent.IsEnabled = true;
            this.Progress.Visibility = Visibility.Collapsed;
            this._isBusy = false;
        }
    }

    private async Task ReloadAsync()
    {
        var workspace = this._settings.Workspaces.FirstOrDefault(item => item.Id == this._workspaceId);

        if (workspace is null)
        {
            this._selected = null;
            this.SelectedPanel.IsEnabled = false;
            this.WorkspaceTitle.Text = L10n.T("Комплект удалён");
            this.WorkspacePath.Text = "";
            this.SummaryLabel.Text = "";
            this.RepositoryRows.ItemsSource = null;
            return;
        }

        await this.LoadWorkspaceAsync(workspace);
    }

    private async Task LoadWorkspaceAsync(ProjectWorkspace workspace)
    {
        this._selected = workspace;
        this._settings.SelectedWorkspaceId = workspace.Id;
        this._settings.Save();
        this.WorkspaceTitle.Text = workspace.DisplayName;
        this.WorkspacePath.Text = workspace.RootPath ?? L10n.T("Общая папка не определена");
        this.SelectedPanel.IsEnabled = true;
        var rows = await WorkspaceStateReader.ReadAsync(workspace, this._git);

        this.RepositoryRows.ItemsSource = rows;
        var missing = rows.Count(row => !row.IsAvailable);
        this.SummaryLabel.SetResourceReference(TextBlock.ForegroundProperty, missing > 0 ? "WarningBrush" : "AccentBrush");
        this.SummaryLabel.Text = L10n.T("Репозиториев: {0} · изменённых файлов: {1} · недоступных копий: {2}", rows.Count, rows.Sum(row => row.ChangeCount), missing);
        this.StatusText.Text = L10n.T(missing == 0 ? "Готово" : "Есть недоступные копии");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await this.ExecuteAsync(this.ReloadAsync, "Обновление комплекта…");

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (this._selected is null)
        {
            return;
        }

        var dialog = new WorkspaceNameWindow(this._selected.DisplayName) { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (this._settings.Workspaces.Any(workspace => workspace.Id != this._selected.Id
            && string.Equals(workspace.DisplayName, dialog.WorkspaceName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, L10n.T("Такое название комплекта уже используется."));
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            this._selected.Name = dialog.WorkspaceName;
            this._settings.Save();
            await this.ReloadAsync();
        }, "Переименование комплекта…");
    }

    private async void RemoveRepository_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceRepositoryRow row } || this._selected is null)
        {
            return;
        }

        var workspace = this._selected;
        await this.ExecuteAsync(async () =>
        {
            var service = new WorkspaceService(this._git);
            var preview = await service.PreviewRepositoryRemovalAsync(workspace, row.RepositoryPath, this._settings.Workspaces);
            var remainingRepositories = workspace.RepositoryPaths.Concat(workspace.WorktreePaths.Keys)
                .Where(path => !GitService.PathsEqual(path, row.RepositoryPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var dialog = new RemoveWorkspaceRepositoryWindow(row.Name, preview, remainingRepositories == 0) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var removedLastRepository = await service.RemoveRepositoryAsync(
                workspace,
                preview,
                dialog.DeleteWorkingCopy,
                this._settings.Workspaces,
                allowUncommittedChanges: dialog.DeleteWorkingCopy && preview.ChangedFileCount > 0);

            if (removedLastRepository)
            {
                this._settings.Workspaces.Remove(workspace);
                this._settings.FavoriteWorkspaceIds.Remove(workspace.Id);
                this._settings.RecentWorkspaceIds.Remove(workspace.Id);
                this._settings.SelectedWorkspaceId = null;
                this._selected = null;
            }

            this._settings.Save();
            await this.ReloadAsync();
        }, L10n.T("{0}: удаление из комплекта…", row.Name));
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (this._selected is null)
        {
            return;
        }

        var workspace = this._selected;
        await this.ExecuteAsync(async () =>
        {
            var service = new WorkspaceService(this._git);
            var rootPath = workspace.RootPath;
            await service.ValidateRemovalAsync(workspace, this._settings.Workspaces,
                allowUncommittedChanges: true, rootPathToDelete: rootPath);
            var changedCopies = new List<string>();
            foreach (var path in workspace.WorktreePaths.Values)
            {
                if (rootPath is not null && !Directory.Exists(path))
                {
                    continue;
                }

                var changes = await this._git.GetStatusAsync(path, true);
                if (changes.Count > 0)
                {
                    changedCopies.Add(L10n.T("{0}: незакоммиченных файлов — {1}", path, changes.Count));
                }
            }

            var paths = string.Join("\n", workspace.WorktreePaths.Values);
            var message = rootPath is null
                ? L10n.T("Удалить все рабочие копии этого комплекта?\n\n{0}\n\nВетки и основные репозитории сохранятся.", paths)
                : L10n.T("Удалить комплект вместе с общей папкой?\n\n{0}\n\nБудут удалены все рабочие копии и всё содержимое этой папки. Ветки и основные репозитории сохранятся.", rootPath);

            if (rootPath is not null)
            {
                var extraEntries = Directory.EnumerateFileSystemEntries(rootPath)
                    .Where(path => !workspace.WorktreePaths.Values.Any(copy => GitService.PathsEqual(copy, path)))
                    .Select(Path.GetFileName)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (extraEntries.Length > 0)
                {
                    message += "\n\n" + L10n.T("Также будут удалены файлы и папки рядом с рабочими копиями:\n{0}", string.Join("\n", extraEntries));
                }
            }

            if (changedCopies.Count > 0)
            {
                message += "\n\n" + L10n.T("Есть незакоммиченные изменения:\n{0}\n\nОни будут безвозвратно удалены вместе с рабочими копиями. Продолжить?", string.Join("\n", changedCopies));
            }

            var answer = MessageBox.Show(this, message, L10n.T("Удаление комплекта"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await service.RemoveAsync(workspace, this._settings.Workspaces,
                allowUncommittedChanges: changedCopies.Count > 0, rootPathToDelete: rootPath);
            if (rootPath is not null && result.FailedPath is not null && GitService.PathsEqual(result.FailedPath, rootPath))
            {
                await this.ReloadAsync();
                this.StatusText.Text = L10n.T("Общая папка не удалена");
                MessageBox.Show(this, L10n.T("Рабочие копии удалены, но не удалось удалить общую папку:\n{0}\n\n{1}", rootPath, L10n.T(result.Error ?? string.Empty)),
                    L10n.T("Удаление остановлено"), MessageBoxButton.OK, MessageBoxImage.Warning);

                return;
            }

            workspace.WorktreePaths = workspace.WorktreePaths.Where(pair => !result.RemovedPaths.Any(path => GitService.PathsEqual(path, pair.Value)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            workspace.RepositoryPaths = workspace.RepositoryPaths.Where(repositoryPath =>
                !result.RemovedPaths.Any(path => GitService.PathsEqual(path, repositoryPath))
                && workspace.WorktreePaths.Keys.Any(path => GitService.PathsEqual(path, repositoryPath))).ToList();

            if (workspace.PrimaryRepositoryPath is not null
                && !workspace.RepositoryPaths.Any(path => GitService.PathsEqual(path, workspace.PrimaryRepositoryPath)))
            {
                workspace.PrimaryRepositoryPath = workspace.RepositoryPaths.FirstOrDefault();
            }

            if (workspace.RepositoryPaths.Count == 0)
            {
                this._settings.Workspaces.Remove(workspace);
                this._settings.FavoriteWorkspaceIds.Remove(workspace.Id);
                this._settings.RecentWorkspaceIds.Remove(workspace.Id);
                this._settings.SelectedWorkspaceId = null;
                this._selected = null;
            }

            this._settings.Save();
            await this.ReloadAsync();

            if (result.FailedPath is not null)
            {
                MessageBox.Show(this, L10n.T("Удалено копий: {0}.\nНе удалось удалить {1}:\n{2}\n\nОставшиеся копии сохранены в комплекте.", result.RemovedPaths.Count, result.FailedPath, L10n.T(result.Error ?? string.Empty)), L10n.T("Удаление остановлено"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, "Проверка всех копий перед удалением…");
    }
}
