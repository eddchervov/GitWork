using System.Text;

namespace GitWork.Core;

public sealed class GitFileActionsService
{
    private readonly GitService _git;
    private readonly GitIndexService _index;

    public GitFileActionsService(GitService git)
    {
        this._git = git;
        this._index = new GitIndexService(git);
    }

    public static string GetFullPath(string worktree, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Any(char.IsControl))
        {
            throw new GitException("Некорректный путь файла.");
        }

        var normalized = relativePath.Replace('\\', '/').TrimEnd('/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new GitException("Путь файла выходит за пределы рабочей копии или указывает на служебные данные Git.");
        }

        var root = Path.GetFullPath(worktree);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Путь файла выходит за пределы рабочей копии.");
        }

        return fullPath;
    }

    public async Task<string> AddIgnoreRuleAsync(string worktree, string relativePath, bool directory)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        GitFileActionsService.GetFullPath(root, relativePath);
        var normalized = relativePath.Replace('\\', '/').TrimEnd('/');
        var rule = new StringBuilder("/");
        foreach (var character in normalized)
        {
            if (character is '\\' or '*' or '?' or '[' or ']' or '#' or '!' or ' ')
            {
                rule.Append('\\');
            }

            rule.Append(character);
        }

        if (directory)
        {
            rule.Append('/');
        }

        var ignorePath = Path.Combine(root, ".gitignore");
        GitFileActionsService.EnsureRegularPath(root, ignorePath);
        await using var stream = new FileStream(ignorePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 1_000_000)
        {
            throw new GitException("Файл .gitignore слишком большой для автоматического изменения.");
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            throw new GitException("Кодировка .gitignore отличается от UTF-8. Добавьте правило вручную, чтобы сохранить исходную кодировку.");
        }

        var pattern = rule.ToString();
        if (text.Split('\n').Any(line => line.TrimEnd('\r') == pattern))
        {
            return pattern;
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        var separator = text.Length > 0 && !text.EndsWith('\n') ? newline : "";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(separator + pattern + newline));

        return pattern;
    }

    public async Task<SelectedCommitPreview> GetDiscardPreviewAsync(string worktree, ChangedFile file)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        if (file.IsDirectory || file.IsGitLink)
        {
            throw new GitException("Рабочие копии и вложенные репозитории не удаляются через отмену изменений файла.");
        }

        foreach (var path in GitFileActionsService.GetPaths(file))
        {
            GitFileActionsService.EnsureRegularPath(root, GitFileActionsService.GetFullPath(root, path));
        }

        return await this._index.GetSelectedCommitPreviewAsync(root, [file]);
    }

    public async Task<SelectedCommitPreview> GetDiscardAllPreviewAsync(string worktree)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        var status = await this._git.GetStatusAsync(root, includeAllUntrackedFiles: true);
        var files = status.Where(file => !file.IsDirectory && !file.IsGitLink)
            .GroupBy(file => file.Path, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(file => file.Status != "??") ?? group.First())
            .ToArray();
        if (files.Length == 0)
        {
            throw new GitException("Нет обычных файлов с изменениями для отмены.");
        }

        foreach (var file in files)
        {
            foreach (var path in GitFileActionsService.GetPaths(file))
            {
                GitFileActionsService.EnsureRegularPath(root, GitFileActionsService.GetFullPath(root, path));
            }
        }

        return await this._index.GetSelectedCommitPreviewAsync(root, files);
    }

    public async Task<string?> DiscardAllAsync(SelectedCommitPreview preview, string? backupRoot = null)
    {
        if (preview.Files.Count == 0)
        {
            throw new GitException("Нет выбранных файлов для отмены изменений.");
        }

        await this.VerifyDiscardPreviewAsync(preview);
        var paths = preview.Files
            .SelectMany(file => GitFileActionsService.GetPaths(file.File))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var path in paths)
        {
            GitFileActionsService.EnsureRegularPath(preview.Repository, GitFileActionsService.GetFullPath(preview.Repository, path));
        }

        var indexEntries = (await this._git.RunAsync(preview.Repository, "ls-files", "--stage", "-z"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var selectedPaths = paths.ToHashSet(StringComparer.Ordinal);
        var selectedIndexEntries = indexEntries
            .Where(entry => selectedPaths.Contains(entry[(entry.IndexOf('\t') + 1)..]))
            .ToArray();
        if (selectedIndexEntries.Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
        {
            throw new GitException("Рабочие копии и вложенные репозитории нельзя отменить вместе с файлами.");
        }

        var indexPaths = selectedIndexEntries
            .Select(entry => entry[(entry.IndexOf('\t') + 1)..])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existingPaths = paths
            .Where(path => File.Exists(GitFileActionsService.GetFullPath(preview.Repository, path)))
            .ToArray();
        var backup = await this.CreateDiscardBackupAsync(preview.Repository, existingPaths, indexPaths, backupRoot);

        await this.VerifyDiscardPreviewAsync(preview);
        try
        {
            if (preview.HeadHash.Length == 0)
            {
                if (indexPaths.Length > 0)
                {
                    foreach (var batch in GitFileActionsService.ChunkPaths(indexPaths))
                    {
                        await this._git.RunAsync(preview.Repository,
                            ["rm", "--cached", "--force", "--ignore-unmatch", "--", .. batch]);
                    }
                }

                foreach (var path in existingPaths)
                {
                    File.Delete(GitFileActionsService.GetFullPath(preview.Repository, path));
                }
            }
            else
            {
                var trackedPaths = preview.Files
                    .Where(file => file.Status != "??")
                    .SelectMany(file => GitFileActionsService.GetPaths(file.File))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (trackedPaths.Length > 0)
                {
                    foreach (var batch in GitFileActionsService.ChunkPaths(trackedPaths))
                    {
                        await this._git.RunAsync(preview.Repository,
                            ["restore", "--source=" + preview.HeadHash, "--staged", "--worktree", "--", .. batch]);
                    }
                }

                var restoredPaths = trackedPaths.ToHashSet(StringComparer.Ordinal);
                foreach (var file in preview.Files.Where(file => file.Status == "??" && !restoredPaths.Contains(file.File.Path)))
                {
                    File.Delete(GitFileActionsService.GetFullPath(preview.Repository, file.File.Path));
                }
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            throw new GitException(backup is null ? exception.Message : $"{exception.Message}\nРезервная копия: {backup}");
        }

        return backup;
    }

    public async Task<string?> DiscardAsync(SelectedCommitPreview preview, string? backupRoot = null)
    {
        if (preview.Files.Count != 1)
        {
            throw new GitException("Выберите один файл для отмены изменений.");
        }

        var file = preview.Files[0].File;
        await this.VerifyDiscardPreviewAsync(preview);
        var paths = GitFileActionsService.GetPaths(file);
        var indexOutput = await this._git.RunAsync(preview.Repository,
            ["ls-files", "--stage", "-z", "--", .. paths.Select(path => ":(literal)" + path)]);
        var indexEntries = indexOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (indexEntries.Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
        {
            throw new GitException("Рабочие копии и вложенные репозитории не удаляются через отмену изменений файла.");
        }

        var indexPaths = indexEntries.Select(entry => entry[(entry.IndexOf('\t') + 1)..]).ToArray();
        var existingPaths = paths.Where(path => File.Exists(GitFileActionsService.GetFullPath(preview.Repository, path))).ToArray();
        var backup = await this.CreateDiscardBackupAsync(preview.Repository, existingPaths, indexPaths, backupRoot);

        await this.VerifyDiscardPreviewAsync(preview);
        try
        {
            if (preview.Files[0].Status == "??")
            {
                File.Delete(GitFileActionsService.GetFullPath(preview.Repository, file.Path));
            }
            else if (preview.HeadHash.Length == 0)
            {
                await this._git.RunAsync(preview.Repository,
                    ["rm", "--cached", "--force", "--ignore-unmatch", "--", .. paths.Select(path => ":(literal)" + path)]);
                foreach (var path in existingPaths)
                {
                    File.Delete(GitFileActionsService.GetFullPath(preview.Repository, path));
                }
            }
            else
            {
                await this._git.RunAsync(preview.Repository,
                    ["restore", "--source=" + preview.HeadHash, "--staged", "--worktree", "--", .. paths.Select(path => ":(literal)" + path)]);
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            throw new GitException(backup is null ? exception.Message : $"{exception.Message}\nРезервная копия: {backup}");
        }

        return backup;
    }

    private async Task VerifyDiscardPreviewAsync(SelectedCommitPreview preview)
    {
        var current = await this._index.GetSelectedCommitPreviewAsync(
            preview.Repository,
            preview.Files.Select(file => file.File).ToArray());
        if (current.HeadHash != preview.HeadHash || current.IndexSignature != preview.IndexSignature
            || current.Files.Count != preview.Files.Count
            || current.Files.Zip(preview.Files).Any(pair => pair.First.Status != pair.Second.Status
                || pair.First.WorkingFileFingerprint != pair.Second.WorkingFileFingerprint))
        {
            throw new GitException("Файлы или индекс изменились. Обновите список перед отменой изменений.");
        }
    }

    private async Task<string?> CreateDiscardBackupAsync(
        string repository,
        IReadOnlyList<string> existingPaths,
        IReadOnlyList<string> indexPaths,
        string? backupRoot)
    {
        if (existingPaths.Count == 0 && indexPaths.Count == 0)
        {
            return null;
        }

        backupRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitWork", "backups", "discard");
        var backup = Path.Combine(Path.GetFullPath(backupRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var path in existingPaths)
        {
            var target = GitFileActionsService.GetFullPath(Path.Combine(backup, "working"), path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = new FileStream(GitFileActionsService.GetFullPath(repository, path), FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination);
        }

        if (indexPaths.Count > 0)
        {
            var indexRoot = Path.Combine(backup, "index");
            Directory.CreateDirectory(indexRoot);
            var prefix = indexRoot.Replace('\\', '/') + "/";
            foreach (var batch in GitFileActionsService.ChunkPaths(indexPaths, literalPathspecs: false))
            {
                await this._git.RunAsync(repository,
                    ["checkout-index", "--prefix=" + prefix, "--", .. batch]);
            }
        }

        return backup;
    }

    private static string[] GetPaths(ChangedFile file) => file.OriginalPath is null ? [file.Path] : [file.OriginalPath, file.Path];

    private static string LiteralPathspec(string path) => ":(literal)" + path;

    private static IEnumerable<string[]> ChunkPaths(IReadOnlyList<string> paths, bool literalPathspecs = true)
    {
        const int maximumArgumentCharacters = 16_000;
        var batch = new List<string>();
        var argumentCharacters = 0;
        foreach (var path in paths)
        {
            var pathspec = literalPathspecs ? GitFileActionsService.LiteralPathspec(path) : path;
            if (batch.Count > 0 && argumentCharacters + pathspec.Length > maximumArgumentCharacters)
            {
                yield return [.. batch];
                batch.Clear();
                argumentCharacters = 0;
            }

            batch.Add(pathspec);
            argumentCharacters += pathspec.Length;
        }

        if (batch.Count > 0)
        {
            yield return [.. batch];
        }
    }

    private static void EnsureRegularPath(string root, string fullPath)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(current, fullPath).Split(Path.DirectorySeparatorChar).Prepend(""))
        {
            current = Path.Combine(current, segment);
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || current == fullPath && (attributes & FileAttributes.Directory) != 0)
                {
                    throw new GitException("Изменение папок и файлов через символические ссылки недоступно.");
                }
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }
}
