namespace GitWork.Core;

public static class RepositoryScanner
{
    private static readonly HashSet<string> s_excludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "venv", ".venv", "packages",
        "Черновики", "Архив", "Trash", "_archives", "artifacts", "dist", "_tmp"
    };

    public static IReadOnlyList<RepositoryInfo> Scan(string root, CancellationToken cancellationToken = default)
    {
        var result = new List<RepositoryInfo>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((System.IO.Path.GetFullPath(root), 0));

        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(System.IO.Path.Combine(item.Path, ".git"))
                || File.Exists(System.IO.Path.Combine(item.Path, ".git")))
            {
                var name = System.IO.Path.GetFileName(item.Path);
                var group = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.Path)) ?? "Проекты";
                result.Add(new RepositoryInfo(name, item.Path, group));
                continue;
            }

            if (item.Depth >= 5)
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(item.Path))
                {
                    var name = System.IO.Path.GetFileName(directory);

                    if (RepositoryScanner.s_excludedNames.Contains(name)
                        || name.StartsWith('.')
                        || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push((directory, item.Depth + 1));
                }
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
        }

        return result.OrderBy(repository => repository.Group).ThenBy(repository => repository.Name).ToArray();
    }
}
