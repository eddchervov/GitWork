using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView
{
    private readonly Dictionary<string, RepositoryRowState> _repositoryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _repositoryStatusTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Task? _repositoryStatusTask;
    private int _repositoryStatusVersion;
    private int _completedRepositoryStatusVersion = -1;

    private void InitializeRepositoryStatuses()
    {
        this.Loaded += async (_, _) =>
        {
            this._repositoryStatusTimer.Start();
            await this.RefreshRepositoryStatusesAsync();
        };
        this.Unloaded += (_, _) =>
        {
            this._repositoryStatusTimer.Stop();
            this._repositoryStatusVersion++;
            this.CloseBranchPicker();
        };
        this._repositoryStatusTimer.Tick += async (_, _) =>
        {
            if (this._settings.AutoRefresh && this.IsVisible && Window.GetWindow(this)?.IsActive == true && this.CanLeave)
            {
                await this.RefreshRepositoryStatusesAsync();
            }
        };
    }

    private RepositoryRowState GetRepositoryState(string path)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!this._repositoryStates.TryGetValue(key, out var state))
        {
            state = new RepositoryRowState(path);
            this._repositoryStates.Add(key, state);
        }

        return state;
    }

    private void RepositoryRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: RepositoryInfo repository } row)
        {
            row.Tag = this.GetRepositoryState(repository.Path);
        }
    }

    private async Task RefreshRepositoryStatusesAsync()
    {
        while (this._repositoryStatusTask is { IsCompleted: false } pending)
        {
            await pending;
            if (this._completedRepositoryStatusVersion == this._repositoryStatusVersion)
            {
                return;
            }
        }

        if (this._workspaceContext || this._repositories.Count == 0)
        {
            return;
        }

        this._repositoryStatusTask = this.ReadRepositoryStatusesAsync();
        await this._repositoryStatusTask;
    }

    private async Task ReadRepositoryStatusesAsync()
    {
        var version = this._repositoryStatusVersion;
        using var gate = new SemaphoreSlim(4);
        var reads = this._repositories.Select(async repository =>
        {
            await gate.WaitAsync();
            try
            {
                if (version != this._repositoryStatusVersion)
                {
                    return;
                }

                var trees = await this._git.GetWorktreesAsync(repository.Path);
                var tree = trees.FirstOrDefault(copy => !copy.IsBare && !copy.IsPrunable && GitService.PathsEqual(copy.Path, repository.Path));
                if (tree is null)
                {
                    throw new GitException("Рабочая копия недоступна.");
                }

                var files = await this._git.GetStatusAsync(tree.Path, true);
                if (version != this._repositoryStatusVersion)
                {
                    return;
                }

                var state = this.GetRepositoryState(repository.Path);
                if (this._selectedCopy is { } selected && GitService.PathsEqual(selected.Path, tree.Path))
                {
                    if (selected.Branch != tree.Branch || state.HasChanges != (files.Count > 0))
                    {
                        this.CloseBranchPicker();
                    }

                    this._selectedCopy = tree;
                    this.CurrentBranchLabel.Text = tree.BranchLabel;
                    this.ChangesTab.Content = files.Count > 0 ? L10n.T("Изменения {0}", files.Count) : L10n.T("Изменения");
                }

                state.Update(tree, files.Count);
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                if (version == this._repositoryStatusVersion)
                {
                    this.GetRepositoryState(repository.Path).Update(null, 0, exception.Message);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(reads);
        if (version == this._repositoryStatusVersion)
        {
            await this.RefreshSyncStateAsync();
            this._completedRepositoryStatusVersion = version;
        }
    }
}
