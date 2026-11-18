using System.IO;
using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView
{
    private async Task PublishCurrentBranchAsync()
    {
        if (this._syncBusy || !this.CanLeave || this._selectedCopy is not { } tree
            || this._syncState is not { CanPublish: true } expectedState
            || !GitService.PathsEqual(tree.Path, expectedState.Worktree))
        {
            return;
        }

        var dialog = new PublishBranchWindow(this._git, expectedState,
            (this.SyncRemotePicker.SelectedItem as GitRemoteInfo)?.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        this._syncBusy = true;
        this.UpdateSyncButtons();
        try
        {
            await this.RunAsync(async () =>
            {
                var result = await new GitRemoteService(this._git).PublishAsync(
                    tree.Path, expectedState, dialog.RemoteName, dialog.RemoteBranchName);
                var message = L10n.T(result.Message);
                try
                {
                    if (this._selectedCopy is { } selected && GitService.PathsEqual(selected.Path, tree.Path))
                    {
                        await this.ShowCurrentViewAsync();
                        await this.RefreshRepositoryStatusesAsync();
                    }
                }
                catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException
                    or ArgumentException or InvalidOperationException)
                {
                    message += "\n" + L10n.T("Не удалось обновить список: {0}", L10n.T(exception.Message));
                }

                this.SetStatus(message);
            });
        }
        finally
        {
            await this.RefreshSyncStateAsync();
            this._syncBusy = false;
            this.UpdateSyncButtons();
        }
    }
}
