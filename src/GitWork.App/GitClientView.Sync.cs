using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView
{
    private RemoteRepositoryState? _syncState;
    private int _syncStateVersion;
    private bool _updatingSyncRemote;
    private bool _syncBusy;

    private async Task RefreshSyncStateAsync()
    {
        if (this._selectedCopy is not { } tree)
        {
            this.ClearSyncState();
            return;
        }

        var version = ++this._syncStateVersion;
        var path = tree.Path;
        var selectedRemoteName = (this.SyncRemotePicker.SelectedItem as GitRemoteInfo)?.Name;
        RemoteRepositoryState state;

        try
        {
            state = await new GitRemoteService(this._git).ReadAsync(path);
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!this.IsCurrentSyncSelection(version, path))
            {
                return;
            }

            this.ClearSyncState();
            this.SyncFetchButton.ToolTip = L10n.T(exception.Message);
            this.SyncPullButton.ToolTip = L10n.T(exception.Message);
            this.SyncPushButton.ToolTip = L10n.T(exception.Message);
            return;
        }

        if (!this.IsCurrentSyncSelection(version, path))
        {
            return;
        }

        this._syncState = state;
        this._updatingSyncRemote = true;
        this.SyncRemotePicker.ItemsSource = state.Remotes;
        this.SyncRemotePicker.SelectedItem = state.Remotes.FirstOrDefault(remote => remote.Name == selectedRemoteName)
            ?? state.Remotes.FirstOrDefault(remote => remote.Name == state.UpstreamRemote)
            ?? state.Remotes.FirstOrDefault();
        this.SyncRemotePicker.Visibility = state.Remotes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        this._updatingSyncRemote = false;
        this.UpdateSyncButtons();
    }

    private void ClearSyncState()
    {
        this._syncStateVersion++;
        this._syncState = null;
        this._updatingSyncRemote = true;
        this.SyncRemotePicker.ItemsSource = null;
        this.SyncRemotePicker.SelectedItem = null;
        this.SyncRemotePicker.Visibility = Visibility.Collapsed;
        this._updatingSyncRemote = false;
        this.UpdateSyncButtons();
    }

    private void SyncRemote_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!this._updatingSyncRemote)
        {
            this.UpdateSyncButtons();
        }
    }

    private async void SyncFetch_Click(object sender, RoutedEventArgs e) => await this.SynchronizeAsync(SyncOperation.Fetch);

    private async void SyncPull_Click(object sender, RoutedEventArgs e) => await this.SynchronizeAsync(SyncOperation.Pull);

    private async void SyncPush_Click(object sender, RoutedEventArgs e) => await this.SynchronizeAsync(SyncOperation.Push);

    private async Task SynchronizeAsync(SyncOperation operation)
    {
        if (this._syncBusy || !this.CanLeave || this._selectedCopy is not { } tree || this._syncState is not { } expectedState
            || !GitService.PathsEqual(tree.Path, expectedState.Worktree))
        {
            return;
        }

        var remote = this.SyncRemotePicker.SelectedItem as GitRemoteInfo;
        if (operation == SyncOperation.Fetch && remote is null
            || operation == SyncOperation.Pull && !expectedState.CanPull
            || operation == SyncOperation.Push && !expectedState.CanPush)
        {
            return;
        }

        var path = tree.Path;
        var didRefreshView = false;
        string? operationMessage = null;
        this._syncBusy = true;
        this.UpdateSyncButtons();
        try
        {
            await this.RunAsync(async () =>
            {
                var service = new GitRemoteService(this._git);
                var result = operation switch
                {
                    SyncOperation.Fetch => await service.FetchAsync(path, remote!.Name),
                    SyncOperation.Pull => await service.PullAsync(path),
                    SyncOperation.Push => await service.PushAsync(path, expectedState),
                    _ => throw new InvalidOperationException("Неизвестная операция синхронизации.")
                };
                operationMessage = result.Message;

                if (this._selectedCopy is not { } selected || !GitService.PathsEqual(selected.Path, path))
                {
                    return;
                }

                await this.ShowCurrentViewAsync();
                didRefreshView = true;
                await this.RefreshRepositoryStatusesAsync();
            });

            if (!didRefreshView)
            {
                await this.RefreshSyncStateAsync();
            }

            if (operationMessage is not null)
            {
                this.ErrorText.Text = L10n.T(operationMessage);
            }
        }
        finally
        {
            this._syncBusy = false;
            this.UpdateSyncButtons();
        }
    }

    private void UpdateSyncButtons()
    {
        var state = this._syncState;
        var remote = this.SyncRemotePicker.SelectedItem as GitRemoteInfo;
        var pushRemote = state?.Remotes.FirstOrDefault(item => item.Name == state.UpstreamRemote);
        this.SyncFetchButton.Content = L10n.T("Fetch");
        this.SyncPullButton.Content = state is { Behind: > 0 } ? L10n.T("Pull ↓{0}", state.Behind) : L10n.T("Pull");
        this.SyncPushButton.Content = state is { Ahead: > 0 } ? L10n.T("Push ↑{0}", state.Ahead) : L10n.T("Push");
        this.SyncRemotePicker.IsEnabled = !this._syncBusy && state is not null && state.Remotes.Count > 1;
        this.SyncFetchButton.IsEnabled = !this._syncBusy && state is not null && remote is not null;
        this.SyncPullButton.IsEnabled = !this._syncBusy && state?.CanPull == true;
        this.SyncPushButton.IsEnabled = !this._syncBusy && state?.CanPush == true;
        this.SyncRemotePicker.ToolTip = remote?.Url;
        this.SyncFetchButton.ToolTip = remote is null
            ? L10n.T("В репозитории не настроен сервер для Fetch.")
            : L10n.T("Fetch: {0}\n{1}\nЗагрузить коммиты и обновить сведения о ветках. Рабочие файлы не меняются.", remote.Name, remote.Url);
        this.SyncPullButton.ToolTip = state?.CanPull == true
            ? L10n.T("Загрузить изменения upstream и обновить текущую ветку только fast-forward.")
            : L10n.T(state?.PullUnavailableReason ?? "Выберите репозиторий");
        this.SyncPushButton.ToolTip = state?.CanPush == true
            ? L10n.T("Отправить только коммиты текущей ветки в её upstream. Принудительная отправка не используется.\nАдрес: {0}", pushRemote?.DisplayPushUrl ?? "")
            : L10n.T(state?.PushUnavailableReason ?? "Выберите репозиторий");
    }

    private bool IsCurrentSyncSelection(int version, string path) => version == this._syncStateVersion
        && this._selectedCopy is { } selected && GitService.PathsEqual(selected.Path, path);

    private enum SyncOperation
    {
        Fetch,
        Pull,
        Push
    }
}
