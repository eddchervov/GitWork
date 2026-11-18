using System.Globalization;
using System.Text.RegularExpressions;

namespace GitWork.Core;

public sealed class GitStashService
{
    private const int MaximumPatchLength = 500_000;
    private static readonly Regex s_droppedHash = new("\\(([0-9a-fA-F]{40}|[0-9a-fA-F]{64})\\)\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly GitService _git;

    public GitStashService(GitService git)
    {
        this._git = git;
    }

    public async Task<IReadOnlyList<GitStashEntry>> GetAsync(string worktree)
    {
        var repository = await this._git.GetRepositoryRootAsync(worktree);
        try
        {
            await this._git.RunAsync(repository, "rev-parse", "--verify", "--end-of-options", "refs/stash");
        }
        catch (GitException)
        {
            return [];
        }

        var output = await this._git.RunAsync(repository, "log", "-g", "-z", "--no-color", "--format=%H%x00%gd%x00%gs%x00%ct", "refs/stash");
        var fields = output.Split('\0');
        var entries = new List<GitStashEntry>();
        for (var index = 0; index + 3 < fields.Length; index += 4)
        {
            if (!long.TryParse(fields[index + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            {
                continue;
            }

            entries.Add(new GitStashEntry(fields[index], fields[index + 1], GitStashService.GetMessage(fields[index + 2]),
                DateTimeOffset.FromUnixTimeSeconds(timestamp)));
        }

        return entries;
    }

    public async Task CreateAsync(string worktree, string message, bool includeUntracked)
    {
        if (message.Any(char.IsControl))
        {
            throw new GitException("Название сохранения не должно содержать управляющие символы.");
        }

        await this._git.GetRepositoryRootAsync(worktree);
        await this.EnsureNoOperationInProgressAsync(worktree);
        var changes = await this._git.GetStatusAsync(worktree, includeAllUntrackedFiles: true);
        if (!changes.Any(file => file.Status != "??" || includeUntracked && !file.IsGitLink))
        {
            var hasNestedRepositories = includeUntracked && changes.Any(file => file.IsGitLink);

            throw new GitException(hasNestedRepositories
                ? "Нет изменений для сохранения. Вложенный Git-репозиторий нельзя включить в stash родительского репозитория."
                : "Нет изменений для сохранения. Пустое сохранение не создано.");
        }

        var stashBefore = await this.GetAsync(worktree);
        var changesBefore = changes;
        var arguments = new List<string> { "stash", "push" };
        if (!string.IsNullOrWhiteSpace(message))
        {
            arguments.AddRange(["--message", message]);
        }

        if (includeUntracked)
        {
            arguments.Add("--include-untracked");
        }

        await this._git.RunAsync(worktree, arguments.ToArray());
        var stashAfter = await this.GetAsync(worktree);
        var changesAfter = await this._git.GetStatusAsync(worktree, includeAllUntrackedFiles: true);
        if (GitStashService.SameStashSnapshot(stashBefore, stashAfter)
            && GitStashService.SameStatusSnapshot(changesBefore, changesAfter))
        {
            throw new GitException("Git не сохранил изменения: stash и рабочая копия не изменились. Вложенные репозитории могут быть пропущены.");
        }
    }

    public async Task ApplyAsync(string worktree, GitStashEntry entry, bool removeAfterApply)
    {
        var repository = await this._git.GetRepositoryRootAsync(worktree);
        var currentEntry = await this.ResolveEntryAsync(repository, entry);
        await this.EnsureNoOperationInProgressAsync(worktree);
        var changes = await this._git.GetStatusAsync(worktree, includeAllUntrackedFiles: true);
        if (changes.Any(file => file.Status != "??"))
        {
            throw new GitException("Сначала сохраните или отмените отслеживаемые изменения в рабочей копии.");
        }

        await this.EnsureNoRestorePathCollisionsAsync(worktree, currentEntry, changes);

        await this._git.RunAsync(worktree, "stash", "apply", "--index", currentEntry.Hash);
        if (removeAfterApply)
        {
            await this.DropAsync(worktree, currentEntry);
        }
    }

    public async Task DropAsync(string worktree, GitStashEntry entry)
    {
        var repository = await this._git.GetRepositoryRootAsync(worktree);
        var before = await this.GetAsync(repository);
        var currentEntry = this.ResolveEntry(entry, before);
        var output = await this._git.RunAsync(repository, "stash", "drop", "--no-quiet", currentEntry.Reference);
        var droppedHashMatch = GitStashService.s_droppedHash.Match(output.TrimEnd('\r', '\n'));
        var remaining = await this.GetAsync(repository);
        if (!droppedHashMatch.Success)
        {
            var missingHashes = GitStashService.GetMissingHashes(before, remaining, null);
            await this.RecoverStashHashesAsync(repository, missingHashes);
            throw new GitException("Git удалил stash, но не сообщил его hash. Обновите список; удалённые записи восстановлены, если они были потеряны.");
        }

        var droppedHash = droppedHashMatch.Groups[1].Value;
        if (!string.Equals(droppedHash, currentEntry.Hash, StringComparison.OrdinalIgnoreCase))
        {
            var hashesToRecover = GitStashService.GetMissingHashes(before, remaining, null).ToList();
            if (!hashesToRecover.Contains(droppedHash, StringComparer.OrdinalIgnoreCase))
            {
                hashesToRecover.Add(droppedHash);
            }

            await this.RecoverStashHashesAsync(repository, hashesToRecover);
            throw new GitException("Порядок stash изменился во время удаления. Удалённая запись восстановлена; обновите список и повторите действие.");
        }

        var beforeCount = before.Count(item => item.Hash == currentEntry.Hash);
        var remainingCount = remaining.Count(item => item.Hash == currentEntry.Hash);
        if (remainingCount != beforeCount - 1)
        {
            var hashesToRecover = GitStashService.GetMissingHashes(before, remaining, currentEntry.Hash);
            await this.RecoverStashHashesAsync(repository, hashesToRecover);
            throw new GitException("Список stash изменился во время удаления. Удаление не подтверждено; обновите список.");
        }
    }

    private GitStashEntry ResolveEntry(GitStashEntry entry, IReadOnlyList<GitStashEntry> currentEntries)
    {
        if (!GitStashService.IsObjectHash(entry.Hash))
        {
            throw new GitException("Сохранение изменений имеет неверный идентификатор. Обновите список.");
        }

        var referencedEntry = currentEntries.FirstOrDefault(item => item.Reference == entry.Reference
            && string.Equals(item.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase));
        if (referencedEntry is not null)
        {
            return referencedEntry;
        }

        var matchingEntries = currentEntries.Where(item => string.Equals(item.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingEntries.Length == 0)
        {
            throw new GitException("Сохранение изменений больше не найдено. Обновите список.");
        }

        if (matchingEntries.Length > 1)
        {
            throw new GitException("Найдено несколько одинаковых сохранений. Обновите список перед операцией.");
        }

        return matchingEntries[0];
    }

    private async Task RecoverStashHashesAsync(string repository, IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
        {
            var recoveryReference = $"refs/gitwork/stash-recovery/{Guid.NewGuid():N}";
            await this._git.RunAsync(repository, "update-ref", recoveryReference, hash, new string('0', hash.Length));
            try
            {
                var subject = (await this._git.RunAsync(repository, "show", "-s", "--format=%s", hash)).TrimEnd('\r', '\n');
                var beforeCount = (await this.GetAsync(repository)).Count(item => item.Hash == hash);
                await this._git.RunAsync(repository, "stash", "store", $"--message={subject}", hash);
                var afterCount = (await this.GetAsync(repository)).Count(item => item.Hash == hash);
                if (afterCount != beforeCount + 1)
                {
                    throw new GitException("Git не подтвердил восстановление записи stash.");
                }

                await this._git.RunAsync(repository, "update-ref", "-d", recoveryReference, hash);
            }
            catch (GitException exception)
            {
                throw new GitException($"Не удалось восстановить stash. Объект сохранён по ссылке {recoveryReference}. {exception.Message}");
            }
        }
    }

    private static IReadOnlyList<string> GetMissingHashes(
        IReadOnlyList<GitStashEntry> before,
        IReadOnlyList<GitStashEntry> remaining,
        string? expectedDropHash)
    {
        var missing = new List<string>();
        foreach (var group in before.GroupBy(item => item.Hash, StringComparer.OrdinalIgnoreCase))
        {
            var missingCount = group.Count() - remaining.Count(item => string.Equals(item.Hash, group.Key, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(group.Key, expectedDropHash, StringComparison.OrdinalIgnoreCase) && missingCount > 0)
            {
                missingCount--;
            }

            for (var index = 0; index < missingCount; index++)
            {
                missing.Add(group.Key);
            }
        }

        return missing;
    }

    private async Task<GitStashEntry> ResolveEntryAsync(string repository, GitStashEntry entry)
    {
        var currentEntries = await this.GetAsync(repository);
        return this.ResolveEntry(entry, currentEntries);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetFilesAsync(
        string worktree,
        GitStashEntry entry,
        ChangeScope scope = ChangeScope.WorkingTree)
    {
        var repository = await this._git.GetRepositoryRootAsync(worktree);
        var currentEntry = await this.ResolveEntryAsync(repository, entry);
        var trackedOutput = await this._git.RunAsync(repository, "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--name-status", "-z",
            "--find-renames", $"{currentEntry.Hash}^1", scope == ChangeScope.Staged ? $"{currentEntry.Hash}^2" : currentEntry.Hash, "--");
        var files = GitChangeService.ParseNameStatus(trackedOutput).ToDictionary(file => file.Path, StringComparer.Ordinal);

        if (scope == ChangeScope.WorkingTree && await GitStashService.HasThirdParentAsync(this._git, repository, currentEntry.Hash))
        {
            var untrackedOutput = await this._git.RunAsync(repository, "ls-tree", "-r", "-z", "--name-only", $"{currentEntry.Hash}^3");
            foreach (var path in untrackedOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                files.TryAdd(path, new ChangedFile("??", path, null));
            }
        }

        return files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<FileDiff> GetDiffAsync(
        string worktree,
        GitStashEntry entry,
        ChangedFile file,
        ChangeScope scope = ChangeScope.WorkingTree)
    {
        var repository = await this._git.GetRepositoryRootAsync(worktree);
        var currentEntry = await this.ResolveEntryAsync(repository, entry);
        var files = await this.GetFilesAsync(repository, currentEntry, scope);
        var currentFile = files.FirstOrDefault(item => item.Path == file.Path);
        if (currentFile is null)
        {
            throw new GitException("Файл больше не входит в выбранное сохранение изменений.");
        }

        var isUntracked = scope == ChangeScope.WorkingTree && currentFile.Status == "??";
        var oldRevision = $"{currentEntry.Hash}^1";
        var newRevision = scope == ChangeScope.Staged ? $"{currentEntry.Hash}^2" : currentEntry.Hash;
        var oldPath = currentFile.OriginalPath ?? currentFile.Path;
        var pathspecs = currentFile.OriginalPath is null || currentFile.OriginalPath == currentFile.Path
            ? new[] { ":(literal)" + currentFile.Path }
            : new[] { ":(literal)" + currentFile.OriginalPath, ":(literal)" + currentFile.Path };
        var arguments = (isUntracked
                ? new[] { "show", "--format=", "--root", "--no-color", "--no-ext-diff", "--no-textconv", "--unified=3", $"{currentEntry.Hash}^3", "--" }
                : new[] { "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", oldRevision, newRevision, "--" })
            .Concat(pathspecs)
            .ToArray();
        var patch = await this._git.RunLimitedAsync(repository, GitStashService.MaximumPatchLength, arguments);
        var isTruncated = patch.Length == GitStashService.MaximumPatchLength;
        var diff = GitChangeService.ParseUnifiedDiff(patch, isTruncated);
        if (DiffContextLoader.CanLoad(diff))
        {
            diff = diff with
            {
                LoadContextAsync = () => DiffContextLoader.ReadAsync(this._git, repository,
                    isUntracked ? null : $"{currentEntry.Hash}^1", oldPath,
                    isUntracked ? $"{currentEntry.Hash}^3" : scope == ChangeScope.Staged ? $"{currentEntry.Hash}^2" : currentEntry.Hash,
                    currentFile.Path)
            };
        }

        return diff;
    }

    private async Task EnsureNoOperationInProgressAsync(string worktree)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
        {
            var path = (await this._git.RunAsync(worktree, "rev-parse", "--git-path", marker)).TrimEnd('\r', '\n');
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(Path.Combine(worktree, path));
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new GitException("Нельзя выполнять операцию со stash, пока в рабочей копии выполняется другая операция Git.");
            }
        }
    }

    private async Task EnsureNoRestorePathCollisionsAsync(
        string worktree,
        GitStashEntry entry,
        IReadOnlyList<ChangedFile> changes)
    {
        var workingFiles = await this.GetFilesAsync(worktree, entry, ChangeScope.WorkingTree);
        var stagedFiles = await this.GetFilesAsync(worktree, entry, ChangeScope.Staged);
        var restorePaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in workingFiles.Concat(stagedFiles))
        {
            restorePaths[file.Path] = restorePaths.TryGetValue(file.Path, out var wasUntracked)
                ? wasUntracked || file.Status == "??"
                : file.Status == "??";
            if (file.OriginalPath is not null)
            {
                restorePaths.TryAdd(file.OriginalPath, false);
            }
        }

        var untrackedChanges = changes.Where(file => file.Status == "??").ToArray();
        if (untrackedChanges.Any(untracked => restorePaths.Keys.Any(path => GitStashService.PathsOverlap(untracked.Path, path))))
        {
            throw new GitException("В рабочей копии есть новые файлы по путям из сохранения. Уберите их перед применением stash.");
        }

        if (restorePaths.Count == 0)
        {
            return;
        }

        var trackedOutput = await this._git.RunAsync(worktree, "ls-files", "-z");
        var trackedPaths = new HashSet<string>(trackedOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(worktree);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (relativePath, isUntrackedInStash) in restorePaths)
        {
            if (isUntrackedInStash && trackedPaths.Contains(relativePath))
            {
                throw new GitException("В рабочей копии уже есть отслеживаемый файл по пути из сохранения.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitException("Путь из сохранения выходит за пределы рабочей копии.");
            }

            var isTracked = trackedPaths.Contains(relativePath);
            if ((Directory.Exists(fullPath) || File.Exists(fullPath)) && !isTracked)
            {
                throw new GitException("В рабочей копии уже есть неотслеживаемый или ignored-файл по пути из сохранения.");
            }

            var parentPath = Path.GetDirectoryName(fullPath);
            while (parentPath is not null && parentPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(parentPath))
                {
                    throw new GitException("Путь из сохранения пересекается с существующим файлом рабочей копии.");
                }

                if (Directory.Exists(parentPath) && (File.GetAttributes(parentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new GitException("Путь из сохранения пересекается с символической ссылкой или junction рабочей копии.");
                }

                parentPath = Path.GetDirectoryName(parentPath);
            }
        }
    }

    private static string GetMessage(string subject)
    {
        var separator = subject.IndexOf(": ", StringComparison.Ordinal);
        return separator < 0 ? subject : subject[(separator + 2)..];
    }

    private static bool SameStashSnapshot(IReadOnlyList<GitStashEntry> first, IReadOnlyList<GitStashEntry> second) =>
        first.Select(entry => (entry.Hash, entry.Reference)).SequenceEqual(second.Select(entry => (entry.Hash, entry.Reference)));

    private static bool SameStatusSnapshot(IReadOnlyList<ChangedFile> first, IReadOnlyList<ChangedFile> second) =>
        first.Select(file => (file.Status, file.Path, file.OriginalPath)).SequenceEqual(
            second.Select(file => (file.Status, file.Path, file.OriginalPath)));

    private static bool PathsOverlap(string first, string second)
    {
        var firstPath = first.TrimEnd('/');
        var secondPath = second.TrimEnd('/');
        return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase)
            || firstPath.StartsWith(secondPath + "/", StringComparison.OrdinalIgnoreCase)
            || secondPath.StartsWith(firstPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasThirdParentAsync(GitService git, string repository, string hash)
    {
        try
        {
            await git.RunAsync(repository, "rev-parse", "--verify", "--end-of-options", $"{hash}^3");
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    private static bool IsObjectHash(string hash) => hash.Length is 40 or 64
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
