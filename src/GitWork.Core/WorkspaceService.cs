namespace GitWork.Core;

public sealed class WorkspaceService(GitService git)
{
    private readonly GitService _git = git;

    public async Task<WorkspaceRepositoryRemovalPreview> PreviewRepositoryRemovalAsync(
        ProjectWorkspace workspace,
        string repositoryPath,
        IReadOnlyCollection<ProjectWorkspace> allWorkspaces)
    {
        if (!workspace.RepositoryPaths.Concat(workspace.WorktreePaths.Keys)
            .Any(path => GitService.PathsEqual(path, repositoryPath)))
        {
            throw new GitException("Репозиторий больше не входит в этот комплект.");
        }

        var worktreePath = workspace.WorktreePaths.FirstOrDefault(pair => GitService.PathsEqual(pair.Key, repositoryPath)).Value;

        if (worktreePath is null || !Directory.Exists(worktreePath))
        {
            return new WorkspaceRepositoryRemovalPreview(
                workspace.Id,
                workspace.RootPath,
                repositoryPath,
                worktreePath,
                false,
                0,
                0,
                false,
                "Рабочая копия уже отсутствует. Из комплекта можно убрать только запись.");
        }

        var trees = await this._git.GetWorktreesAsync(repositoryPath);
        var tree = trees.FirstOrDefault(item => GitService.PathsEqual(item.Path, worktreePath));

        if (tree is null || tree.IsBare || tree.IsPrunable)
        {
            return new WorkspaceRepositoryRemovalPreview(
                workspace.Id,
                workspace.RootPath,
                repositoryPath,
                worktreePath,
                true,
                0,
                0,
                false,
                "Это основная, заблокированная или недоступная рабочая копия.");
        }

        var changes = await this._git.GetStatusAsync(worktreePath, true);
        var ignored = await this._git.RunAsync(worktreePath, "ls-files", "--others", "--ignored", "--exclude-standard", "-z");
        var ignoredCount = ignored.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        string? unavailableReason = null;

        if (tree.IsMain || tree.IsLocked)
        {
            unavailableReason = "Это основная или заблокированная рабочая копия.";
        }
        else if (allWorkspaces.Where(item => item.Id != workspace.Id)
            .Any(item => item.WorktreePaths.Values.Any(path => GitService.PathsEqual(path, worktreePath))))
        {
            unavailableReason = "Эта рабочая копия используется другим комплектом.";
        }
        else if (changes.Count > 0)
        {
            unavailableReason = "Сначала сохраните или закоммитьте изменения.";
        }
        else if (ignoredCount > 0)
        {
            unavailableReason = $"Найдены игнорируемые файлы ({ignoredCount}). Они могут содержать локальные данные.";
        }

        return new WorkspaceRepositoryRemovalPreview(
            workspace.Id,
            workspace.RootPath,
            repositoryPath,
            worktreePath,
            true,
            changes.Count,
            ignoredCount,
            unavailableReason is null,
            unavailableReason);
    }

    public async Task<bool> RemoveRepositoryAsync(
        ProjectWorkspace workspace,
        WorkspaceRepositoryRemovalPreview preview,
        bool deleteWorkingCopy,
        IReadOnlyCollection<ProjectWorkspace> allWorkspaces)
    {
        var current = await this.PreviewRepositoryRemovalAsync(workspace, preview.RepositoryPath, allWorkspaces);

        if (!string.Equals(current.WorkspaceId, preview.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(current.RootPath, preview.RootPath, StringComparison.OrdinalIgnoreCase)
            || !GitService.PathsEqual(current.RepositoryPath, preview.RepositoryPath)
            || !WorkspaceService.PathsEqualOrBothNull(current.WorktreePath, preview.WorktreePath)
            || current.IsWorkingCopyAvailable != preview.IsWorkingCopyAvailable
            || current.ChangedFileCount != preview.ChangedFileCount
            || current.IgnoredFileCount != preview.IgnoredFileCount)
        {
            throw new GitException("Состав или состояние комплекта изменилось. Обновите окно удаления и проверьте его ещё раз.");
        }

        if (deleteWorkingCopy)
        {
            if (!current.CanDeleteWorkingCopy || current.WorktreePath is null)
            {
                throw new GitException(current.DeleteBlockedReason ?? "Рабочую копию нельзя удалить с диска.");
            }

            await this._git.RemoveWorktreeAsync(current.RepositoryPath, current.WorktreePath);
        }

        workspace.RepositoryPaths.RemoveAll(path => GitService.PathsEqual(path, current.RepositoryPath));
        var worktreeKeys = workspace.WorktreePaths.Keys
            .Where(path => GitService.PathsEqual(path, current.RepositoryPath)).ToArray();

        foreach (var worktreeKey in worktreeKeys)
        {
            workspace.WorktreePaths.Remove(worktreeKey);
        }

        var primaryWasRemoved = workspace.PrimaryRepositoryPath is not null
            && GitService.PathsEqual(workspace.PrimaryRepositoryPath, current.RepositoryPath);

        if (primaryWasRemoved)
        {
            workspace.PrimaryRepositoryPath = workspace.RepositoryPaths.FirstOrDefault() ?? workspace.WorktreePaths.Keys.FirstOrDefault();
        }

        return !workspace.RepositoryPaths.Concat(workspace.WorktreePaths.Keys).Any();
    }

    public async Task ValidateRemovalAsync(
        ProjectWorkspace workspace,
        IReadOnlyCollection<ProjectWorkspace>? allWorkspaces = null)
    {
        if (workspace.WorktreePaths.Count == 0)
        {
            throw new GitException("В комплекте нет рабочих копий.");
        }

        foreach (var pair in workspace.WorktreePaths)
        {
            var trees = await this._git.GetWorktreesAsync(pair.Key);
            var tree = trees.FirstOrDefault(item => GitService.PathsEqual(item.Path, pair.Value));

            if (!WorkspaceService.CanRemove(tree))
            {
                throw new GitException($"Нельзя удалить копию {pair.Value}: она основная, заблокирована или недоступна.");
            }

            if ((await this._git.GetStatusAsync(pair.Value)).Count > 0)
            {
                throw new GitException($"В {pair.Value} есть изменения. Удаление всего комплекта отменено.");
            }

            var ignored = await this._git.RunAsync(pair.Value, "ls-files", "--others", "--ignored", "--exclude-standard", "-z");

            if (ignored.Length > 0)
            {
                throw new GitException($"В {pair.Value} есть игнорируемые файлы. Удаление всего комплекта отменено, чтобы сохранить локальные данные.");
            }

            if (allWorkspaces?.Where(item => item.Id != workspace.Id)
                .Any(item => item.WorktreePaths.Values.Any(path => GitService.PathsEqual(path, pair.Value))) == true)
            {
                throw new GitException($"Копия {pair.Value} используется другим комплектом. Удаление всего комплекта отменено.");
            }
        }
    }

    public async Task<WorkspaceRemovalResult> RemoveAsync(
        ProjectWorkspace workspace,
        IReadOnlyCollection<ProjectWorkspace>? allWorkspaces = null)
    {
        await this.ValidateRemovalAsync(workspace, allWorkspaces);
        var removed = new List<string>();

        foreach (var pair in workspace.WorktreePaths)
        {
            try
            {
                await this._git.RemoveWorktreeAsync(pair.Key, pair.Value);
                removed.Add(pair.Value);
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
            {
                return new WorkspaceRemovalResult(removed, pair.Value, exception.Message);
            }
        }

        return new WorkspaceRemovalResult(removed, null, null);
    }

    private static bool PathsEqualOrBothNull(string? first, string? second) => first is null || second is null
        ? first is null && second is null
        : GitService.PathsEqual(first, second);

    private static bool CanRemove(WorktreeInfo? tree) => tree is
    {
        IsMain: false,
        IsBare: false,
        IsLocked: false,
        IsPrunable: false
    };
}
