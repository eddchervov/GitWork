using System.IO;
using GitWork.Core;

namespace GitWork.App;

public static class WorkspaceStateReader
{
    public static async Task<IReadOnlyList<WorkspaceRepositoryRow>> ReadAsync(ProjectWorkspace workspace, GitService git)
    {
        var rows = new List<WorkspaceRepositoryRow>();
        var repositories = workspace.RepositoryPaths.Concat(workspace.WorktreePaths.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var repositoryPath in repositories)
        {
            var path = workspace.WorktreePaths.FirstOrDefault(pair => GitService.PathsEqual(pair.Key, repositoryPath)).Value;
            var name = Path.GetFileName(repositoryPath.TrimEnd('\\', '/'));

            if (path is null)
            {
                rows.Add(new WorkspaceRepositoryRow { RepositoryPath = repositoryPath, Name = name, Path = "Копия не создана", Error = "Комплект неполный: рабочая копия отсутствует." });
                continue;
            }

            try
            {
                var trees = await git.GetWorktreesAsync(repositoryPath);
                var tree = trees.FirstOrDefault(candidate => GitService.PathsEqual(candidate.Path, path));

                if (tree is null || tree.IsPrunable || tree.IsBare || !Directory.Exists(path))
                {
                    throw new GitException("Рабочая копия недоступна или больше не зарегистрирована в Git.");
                }

                var changes = await git.GetStatusAsync(path, true);
                var branches = (await git.GetBranchesAsync(repositoryPath, trees)).Where(branch => !branch.IsRemote)
                    .Select(branch => branch.OccupiedPath is not null && GitService.PathsEqual(branch.OccupiedPath, path)
                        ? branch with { OccupiedPath = null } : branch).ToArray();

                rows.Add(new WorkspaceRepositoryRow
                {
                    RepositoryPath = repositoryPath,
                    Name = name,
                    Path = path,
                    Worktree = tree,
                    ChangeCount = changes.Count,
                    ChangedFiles = changes,
                    Branches = branches,
                    SelectedBranch = branches.FirstOrDefault(branch => branch.Name == tree.Branch)
                });
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
            {
                rows.Add(new WorkspaceRepositoryRow { RepositoryPath = repositoryPath, Name = name, Path = path, Error = exception.Message });
            }
        }

        return rows;
    }
}
