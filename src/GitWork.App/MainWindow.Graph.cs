using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class MainWindow
{
    private async void OpenGitGraph_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            IReadOnlyList<GraphRepositoryChoice> repositories;
            if (this._activeWorkspace is { } workspace)
            {
                var rows = await WorkspaceStateReader.ReadAsync(workspace, this._git);
                repositories = rows.Where(row => row.IsAvailable).Select(row => new GraphRepositoryChoice(row.Name, row.Path)).ToArray();
            }
            else if (this._worktree is { IsBare: false, IsPrunable: false } tree)
            {
                repositories = [new GraphRepositoryChoice(this._repository?.Name ?? tree.Name, tree.Path)];
            }
            else
            {
                return;
            }

            if (repositories.Count == 0)
            {
                throw new GitException(L10n.T("В комплекте нет доступных рабочих копий."));
            }

            var selected = repositories[0];
            var repository = this._repositories.FirstOrDefault(item => GitService.PathsEqual(item.Path, selected.Path)
                || this._registeredTrees.GetValueOrDefault(item.Path)?.Any(tree => GitService.PathsEqual(tree.Path, selected.Path)) == true)
                ?? new RepositoryInfo(selected.Name, selected.Path, "");
            await this.OpenCopyInClientAsync(repository, selected.Path);
        }, L10n.T("Открытие графа Git…"));
    }
}
