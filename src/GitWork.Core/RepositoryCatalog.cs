using System.Collections.Concurrent;

namespace GitWork.Core;

public static class RepositoryCatalog
{
    public static async Task<(IReadOnlyList<RepositoryInfo> Repositories, IReadOnlyList<string> Errors)> ResolveAsync(
        IEnumerable<RepositoryInfo> candidates,
        GitService git)
    {
        var repositories = new ConcurrentDictionary<string, RepositoryInfo>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (candidate, _) =>
        {
            try
            {
                var worktrees = await git.GetWorktreesAsync(candidate.Path);
                var main = worktrees.FirstOrDefault();

                if (main is null)
                {
                    return;
                }

                var path = System.IO.Path.GetFullPath(main.Path);
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                var group = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "Проекты";
                repositories.TryAdd(path, new RepositoryInfo(name, path, group));
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{candidate.Path}: {exception.Message}");
            }
        });

        var ordered = repositories.Values.OrderBy(repository => repository.Group).ThenBy(repository => repository.Name).ToArray();

        return (ordered, errors.ToArray());
    }
}
