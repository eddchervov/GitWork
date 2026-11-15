namespace GitWork.Core;

public static class WorktreeStateReader
{
    public static async Task<WorktreeState> ReadAsync(WorktreeInfo tree, GitService git)
    {
        if (tree.IsBare || tree.IsPrunable || !Directory.Exists(tree.Path))
        {
            return new WorktreeState(tree, [], tree.IsBare ? "Bare-репозиторий" : "Рабочая папка недоступна");
        }

        try
        {
            var files = await git.GetStatusAsync(tree.Path, true);

            return new WorktreeState(tree, files, null);
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            return new WorktreeState(tree, [], exception.Message);
        }
    }
}
