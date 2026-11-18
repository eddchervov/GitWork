using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class StashWindow : Window
{
    private readonly GitStashService _stash;
    private readonly string _worktree;
    private bool _isBusy;
    private bool _isClosed;
    private bool _statusIsError;
    private int _selectionVersion;
    private int _fileVersion;
    private int _readCount;

    public bool WorkingTreeChanged { get; private set; }

    public bool IsBusy => this._isBusy;

    public StashWindow(GitService git, string worktree)
    {
        this._stash = new GitStashService(git);
        this._worktree = worktree;
        this.InitializeComponent();
        FileContextMenus.Attach(this.FilesList, git, item => item is ChangedFile file
            ? new FileMenuTarget(worktree, file, false) : null, () => this._isBusy);
        this.WorktreePathText.Text = worktree;
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Closing += (_, args) => args.Cancel = this._isBusy;
        this.Closed += (_, _) =>
        {
            this._isClosed = true;
            this._selectionVersion++;
            this._fileVersion++;
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await this.RefreshAsync();
        if (!this._isClosed)
        {
            this.MessageBox.Focus();
        }
    }

    public Task RefreshAsync() => this.RunAsync(async () =>
    {
        await this.LoadEntriesAsync((this.StashesList.SelectedItem as GitStashEntry)?.Hash);
    }, "Чтение сохранений…");

    private async Task LoadEntriesAsync(string? selectedHash)
    {
        var entries = await this._stash.GetAsync(this._worktree);
        if (this._isClosed)
        {
            return;
        }

        this.StashesList.ItemsSource = entries;
        this.StashesList.SelectedItem = entries.FirstOrDefault(entry => entry.Hash == selectedHash) ?? entries.FirstOrDefault();
        this.StashesCount.Text = L10n.T("Сохранения: {0}", entries.Count);
        this.EmptyStashes.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.UpdateActions();
    }

    private async void Stash_Changed(object sender, SelectionChangedEventArgs e) => await this.LoadSelectedFilesAsync();

    private async void Scope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this.FilesList is not null)
        {
            await this.LoadSelectedFilesAsync();
        }
    }

    private async Task LoadSelectedFilesAsync()
    {
        var version = ++this._selectionVersion;
        this._fileVersion++;
        this.FilesList.ItemsSource = null;
        this.FilesCount.Text = L10n.T("Файлы");
        this.FileTitle.Text = L10n.T("Выберите сохранение");
        this.FileTitle.ToolTip = null;
        this.DiffViewer.Visibility = Visibility.Collapsed;
        this.UpdateActions();
        if (this.StashesList.SelectedItem is not GitStashEntry entry)
        {
            return;
        }

        this.FileTitle.Text = L10n.T("Выберите файл");
        this.BeginRead("Чтение файлов сохранения…");
        try
        {
            var scope = this.ScopePicker.SelectedIndex == 1 ? ChangeScope.Staged : ChangeScope.WorkingTree;
            var files = await this._stash.GetFilesAsync(this._worktree, entry, scope);
            if (this._isClosed || version != this._selectionVersion)
            {
                return;
            }

            this.FilesList.ItemsSource = files;
            this.FilesCount.Text = L10n.T("Файлы: {0}", files.Count);
            this.FilesList.SelectedItem = files.FirstOrDefault();
            if (files.Count == 0)
            {
                this.FileTitle.Text = L10n.T("В выбранной области нет изменений.");
            }
        }
        catch (Exception exception) when (StashWindow.IsExpected(exception))
        {
            if (!this._isClosed && version == this._selectionVersion)
            {
                this.SetStatus(L10n.T(exception.Message), isError: true);
            }
        }
        finally
        {
            this.EndRead();
        }
    }

    private async void File_Changed(object sender, SelectionChangedEventArgs e)
    {
        var version = ++this._fileVersion;
        if (this.StashesList.SelectedItem is not GitStashEntry entry || this.FilesList.SelectedItem is not ChangedFile file)
        {
            this.DiffViewer.Visibility = Visibility.Collapsed;
            return;
        }

        this.FileTitle.Text = file.DisplayPath;
        this.FileTitle.ToolTip = file.DisplayPath;
        this.DiffViewer.Visibility = Visibility.Collapsed;
        this.BeginRead("Чтение файла…");
        try
        {
            var scope = this.ScopePicker.SelectedIndex == 1 ? ChangeScope.Staged : ChangeScope.WorkingTree;
            var diff = await this._stash.GetDiffAsync(this._worktree, entry, file, scope);
            if (this._isClosed || version != this._fileVersion)
            {
                return;
            }

            this.DiffViewer.FilePath = file.Path;
            this.DiffViewer.ShowDiff(diff);
            this.DiffViewer.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (StashWindow.IsExpected(exception))
        {
            if (!this._isClosed && version == this._fileVersion)
            {
                this.SetStatus(L10n.T(exception.Message), isError: true);
            }
        }
        finally
        {
            this.EndRead();
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        await this.RunAsync(async () =>
        {
            this.WorkingTreeChanged = true;
            await this._stash.CreateAsync(this._worktree, this.MessageBox.Text.Trim(), this.IncludeUntrackedBox.IsChecked == true);
            this.MessageBox.Clear();
            await this.LoadEntriesAsync(null);
        }, "Сохранение изменений…");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await this.RefreshAsync();

    private async void Apply_Click(object sender, RoutedEventArgs e) => await this.ApplySelectedAsync(false);

    private async void Pop_Click(object sender, RoutedEventArgs e) => await this.ApplySelectedAsync(true);

    private async Task ApplySelectedAsync(bool removeAfterApply)
    {
        if (this.StashesList.SelectedItem is not GitStashEntry entry)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            this.WorkingTreeChanged = true;
            await this._stash.ApplyAsync(this._worktree, entry, removeAfterApply);
            await this.LoadEntriesAsync(entry.Hash);
        }, "Восстановление изменений…");
    }

    private async void Drop_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || this.StashesList.SelectedItem is not GitStashEntry entry)
        {
            return;
        }

        var confirmation = L10n.T("Удалить сохранение «{0}»? Восстановить его через список stash будет нельзя.", entry.Message);
        if (System.Windows.MessageBox.Show(this, confirmation, L10n.T("Удаление сохранения"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            await this._stash.DropAsync(this._worktree, entry);
            await this.LoadEntriesAsync(entry.Hash);
        }, "Удаление сохранения…");
    }

    private async Task RunAsync(Func<Task> action, string message)
    {
        if (this._isBusy || this._isClosed)
        {
            return;
        }

        this._selectionVersion++;
        this._fileVersion++;
        this.SetBusy(true);
        this.SetStatus(L10n.T(message));
        try
        {
            await action();
            if (!this._statusIsError && this._readCount == 0)
            {
                this.SetStatus(L10n.T("Готово"));
            }
        }
        catch (Exception exception) when (StashWindow.IsExpected(exception))
        {
            this.SetStatus(L10n.T(exception.Message), isError: true);
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        this._isBusy = busy;
        this.CreatePanel.IsEnabled = !busy;
        this.ContentPanel.IsEnabled = !busy;
        this.CloseButton.IsEnabled = !busy;
        this.Progress.Visibility = busy || this._readCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.UpdateActions();
    }

    private void UpdateActions()
    {
        var enabled = !this._isBusy && this.StashesList.SelectedItem is GitStashEntry;
        this.ApplyButton.IsEnabled = enabled;
        this.PopButton.IsEnabled = enabled;
        this.DropButton.IsEnabled = enabled;
    }

    private void SetStatus(string message, bool isError = false)
    {
        this._statusIsError = isError;
        this.StatusText.Text = message;
        this.StatusText.ToolTip = message;
    }

    private void BeginRead(string message)
    {
        this._readCount++;
        this.Progress.Visibility = Visibility.Visible;
        this.SetStatus(L10n.T(message));
    }

    private void EndRead()
    {
        this._readCount--;
        if (this._isClosed || this._isBusy || this._readCount > 0)
        {
            return;
        }

        this.Progress.Visibility = Visibility.Collapsed;
        if (!this._statusIsError)
        {
            this.SetStatus(L10n.T("Готово"));
        }
    }

    private static bool IsExpected(Exception exception) => exception is GitException or IOException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException;
}
