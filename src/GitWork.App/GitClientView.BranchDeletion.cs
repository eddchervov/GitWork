using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView
{
    private async Task DeleteBranchAsync(BranchInfo branch)
    {
        if (!this.CanLeave || this._selectedCopy is not { IsBare: false, IsPrunable: false } tree)
        {
            return;
        }

        var service = new GitBranchDeletionService(this._git);
        BranchDeletionPreview? preview = null;
        await this.RunAsync(async () => preview = await service.GetPreviewAsync(tree.Path, branch.Name));
        if (preview is null)
        {
            return;
        }

        var message = preview.IsMerged
            ? $"{L10n.T("Удалить локальную ветку")} «{preview.Name}»?\n\n{preview.Hash}"
            : $"{L10n.T("Ветка не слита. Удалить её принудительно?")}\n\n{preview.Name}\n{preview.Hash}";
        var answer = MessageBox.Show(Window.GetWindow(this), message, L10n.T("Удаление ветки"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            await service.DeleteAsync(tree.Path, preview, force: !preview.IsMerged);
            this._repositoryStatusVersion++;
            var trees = await this._git.GetWorktreesAsync(tree.Path);
            var current = trees.FirstOrDefault(copy => GitService.PathsEqual(copy.Path, tree.Path) && !copy.IsPrunable && !copy.IsBare)
                ?? throw new GitException("Рабочая копия недоступна.");
            var branches = await this._git.GetBranchesAsync(tree.Path, trees);
            var changes = await this._git.GetStatusAsync(tree.Path, includeAllUntrackedFiles: true);
            this._branches = branches;
            this._selectedCopy = current;
            this.CurrentBranchLabel.Text = current.BranchLabel;
            this.GetRepositoryState(tree.Path).Update(current, changes.Count);
            this.BranchPicker.SetBranches(branches, current, changes.Count > 0);
            this.BranchPicker.FocusSearch();
        });
    }
}
