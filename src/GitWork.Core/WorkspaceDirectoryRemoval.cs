namespace GitWork.Core;

internal static class WorkspaceDirectoryRemoval
{
    public static async Task<string> ValidateAsync(
        ProjectWorkspace workspace,
        string rootPath,
        IReadOnlyCollection<ProjectWorkspace> allWorkspaces,
        GitService git)
    {
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new GitException("Укажите полный путь общей папки комплекта.");
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var volumeRoot = Path.GetPathRoot(fullRoot);
        if (volumeRoot is null || GitService.PathsEqual(fullRoot, volumeRoot)
            || workspace.RootPath is not { } workspaceRoot || !GitService.PathsEqual(fullRoot, workspaceRoot)
            || !Directory.Exists(fullRoot))
        {
            throw new GitException("Общую папку комплекта нельзя безопасно определить или удалить.");
        }

        var worktreePaths = workspace.WorktreePaths.Values.Select(Path.GetFullPath).ToArray();
        if (worktreePaths.Length == 0 || worktreePaths.Any(path => !WorkspaceDirectoryRemoval.IsDirectChild(fullRoot, path)))
        {
            throw new GitException("Все рабочие копии должны быть непосредственными подпапками общей папки.");
        }

        WorkspaceDirectoryRemoval.EnsureNoReparsePointsOnPath(fullRoot);

        foreach (var repositoryPath in workspace.RepositoryPaths.Concat(workspace.WorktreePaths.Keys))
        {
            if (WorkspaceDirectoryRemoval.Contains(fullRoot, Path.GetFullPath(repositoryPath)))
            {
                throw new GitException($"Общая папка содержит исходный репозиторий: {repositoryPath}");
            }
        }

        foreach (var foreignWorkspace in allWorkspaces.Where(item => item.Id != workspace.Id))
        {
            var foreignPaths = foreignWorkspace.RepositoryPaths.Concat(foreignWorkspace.WorktreePaths.Keys)
                .Concat(foreignWorkspace.WorktreePaths.Values)
                .Concat(foreignWorkspace.RootPath is { } foreignRoot ? [foreignRoot] : [])
                .Where(Path.IsPathFullyQualified)
                .Select(Path.GetFullPath)
                .ToArray();

            if (foreignPaths.Any(path => WorkspaceDirectoryRemoval.Overlaps(fullRoot, path)))
            {
                throw new GitException("Общая папка содержит путь другого комплекта. Удаление отменено.");
            }
        }

        WorkspaceDirectoryRemoval.EnsureNotInsideRepository(fullRoot);
        await WorkspaceDirectoryRemoval.EnsureSafeContentsAsync(fullRoot, worktreePaths, git);

        foreach (var pair in workspace.WorktreePaths)
        {
            var trees = await git.GetWorktreesAsync(pair.Key);
            var mainPaths = trees.Where(tree => tree.IsMain || tree.IsBare).Select(tree => Path.GetFullPath(tree.Path));
            if (mainPaths.Any(path => WorkspaceDirectoryRemoval.Overlaps(fullRoot, path)))
            {
                throw new GitException("Общая папка содержит основной или bare-репозиторий. Удаление отменено.");
            }
        }

        return fullRoot;
    }

    public static void Delete(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        WorkspaceDirectoryRemoval.EnsureNoReparsePointsOnPath(root);
        WorkspaceDirectoryRemoval.EnsureNoRepositoryMarkers(root);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new GitException($"Перед удалением общей папки найдена ссылка: {file}");
            }

            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(root, recursive: true);
    }

    private static async Task EnsureSafeContentsAsync(string root, IReadOnlyCollection<string> worktrees, GitService git)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var fullPath = Path.GetFullPath(entry);
                if (worktrees.Any(worktree => GitService.PathsEqual(worktree, fullPath)))
                {
                    var worktreeAttributes = File.GetAttributes(fullPath);
                    if ((worktreeAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new GitException($"Рабочая копия является ссылкой на другой каталог: {fullPath}");
                    }

                    if ((worktreeAttributes & FileAttributes.Directory) != 0)
                    {
                        WorkspaceDirectoryRemoval.EnsureSafeWorktreeContents(fullPath);
                    }

                    continue;
                }

                if (Path.GetFileName(fullPath).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GitException($"В общей папке найден вложенный репозиторий: {fullPath}");
                }

                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new GitException($"В общей папке есть ссылка на другой каталог: {fullPath}");
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    continue;
                }

                if (WorkspaceDirectoryRemoval.HasRepositoryMarker(fullPath))
                {
                    throw new GitException($"В общей папке найден вложенный репозиторий: {fullPath}");
                }

                pending.Push(fullPath);
            }
        }

        foreach (var worktree in worktrees.Where(Directory.Exists))
        {
            var repository = await git.GetRepositoryRootAsync(worktree);
            if (!GitService.PathsEqual(repository, worktree))
            {
                throw new GitException($"Путь рабочей копии ведёт к другому репозиторию: {worktree}");
            }
        }
    }

    private static void EnsureSafeWorktreeContents(string worktree)
    {
        var pending = new Stack<string>();
        pending.Push(worktree);

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && WorkspaceDirectoryRemoval.HasRepositoryMarker(entry))
                {
                    throw new GitException($"В рабочей копии найден вложенный репозиторий: {entry}");
                }

                var isGitMarker = Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase);
                var isWorktreeGitFile = !isDirectory && GitService.PathsEqual(directory, worktree) && isGitMarker;
                if (isGitMarker && !isWorktreeGitFile)
                {
                    throw new GitException($"В рабочей копии найден вложенный репозиторий: {entry}");
                }

                if (isDirectory)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsureNoRepositoryMarkers(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            if (WorkspaceDirectoryRemoval.HasRepositoryMarker(directory))
            {
                throw new GitException($"Перед удалением общей папки найден репозиторий: {directory}");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new GitException($"Перед удалением общей папки найдена ссылка: {entry}");
                }

                if (Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GitException($"Перед удалением общей папки найден репозиторий: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsureNoReparsePointsOnPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = fullPath;
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new GitException($"Путь содержит ссылку на другой каталог: {current}");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void EnsureNotInsideRepository(string root)
    {
        var current = root;
        while (current is not null)
        {
            if (WorkspaceDirectoryRemoval.HasRepositoryMarker(current))
            {
                throw new GitException($"Общая папка находится внутри репозитория: {current}");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool HasRepositoryMarker(string path) => Directory.Exists(Path.Combine(path, ".git"))
        || File.Exists(Path.Combine(path, ".git"))
        || (File.Exists(Path.Combine(path, "HEAD"))
            && Directory.Exists(Path.Combine(path, "objects"))
            && Directory.Exists(Path.Combine(path, "refs")));

    private static bool IsDirectChild(string parent, string child) => Path.GetDirectoryName(child) is { } parentPath
        && GitService.PathsEqual(parentPath, parent);

    private static bool Contains(string parent, string child) => GitService.PathsEqual(parent, child)
        || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(string first, string second) => WorkspaceDirectoryRemoval.Contains(first, second)
        || WorkspaceDirectoryRemoval.Contains(second, first);
}
