using System.Security.Cryptography;
using System.Text;

namespace GitWork.Core;

public sealed class GitIndexService
{
    private readonly GitService _git;
    private readonly GitChangeService _changes;

    public GitIndexService(GitService git)
    {
        this._git = git;
        this._changes = new GitChangeService(git);
    }

    public async Task<IndexPreview> GetPreviewAsync(string worktree, ChangedFile file, ChangeScope scope)
    {
        GitIndexService.ValidatePath(file.Path);
        if (file.OriginalPath is not null)
        {
            GitIndexService.ValidatePath(file.OriginalPath);
        }

        var indexSignature = await this.GetIndexSignatureAsync(worktree);
        var headHash = await this.GetHeadHashAsync(worktree);
        var workingFileFingerprint = scope == ChangeScope.WorkingTree
            ? await GitIndexService.GetWorkingFileFingerprintAsync(worktree, file)
            : null;
        var diff = await this._changes.GetRevisionAsync(worktree, file, scope);
        if (workingFileFingerprint is not null
            && !string.Equals(workingFileFingerprint,
                await GitIndexService.GetWorkingFileFingerprintAsync(worktree, file), StringComparison.Ordinal))
        {
            throw new GitException("Файл изменился во время чтения предпросмотра. Обновите изменения.");
        }

        await this.VerifySnapshotAsync(worktree, indexSignature, headHash);
        var hunks = GitIndexService.GetHunkPatches(diff.UnifiedText, out var reason);
        return new IndexPreview(
            Path.GetFullPath(worktree), file, scope, diff,
            indexSignature, headHash, hunks, reason, workingFileFingerprint);
    }

    public Task StageFileAsync(IndexPreview preview) => this.MutateFileAsync(preview, true);

    public Task UnstageFileAsync(IndexPreview preview) => this.MutateFileAsync(preview, false);

    public Task StageHunkAsync(IndexPreview preview, int hunkIndex) => this.ApplyHunkAsync(preview, hunkIndex, false);

    public Task UnstageHunkAsync(IndexPreview preview, int hunkIndex) => this.ApplyHunkAsync(preview, hunkIndex, true);

    public async Task<CommitPreview> GetCommitPreviewAsync(string repository)
    {
        var root = await this._git.GetRepositoryRootAsync(repository);
        var signature = await this.GetIndexSignatureAsync(root);
        var head = await this.GetHeadHashAsync(root);
        var filesOutput = await this._git.RunAsync(root, "diff", "--cached", "--name-only", "-z", "--diff-filter=ACDMRTUXB", "--");
        var stagedFiles = filesOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var status = await this._git.GetStatusAsync(root, true);
        var hasConflicts = status.Any(file => GitIndexService.IsConflict(file.Status));
        await this.VerifySnapshotAsync(root, signature, head);

        return new CommitPreview(root, stagedFiles, signature, head, hasConflicts);
    }

    public async Task CommitAsync(CommitPreview preview, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Contains('\0'))
        {
            throw new GitException("Введите сообщение коммита.");
        }

        if (preview.HasConflicts)
        {
            throw new GitException("Сначала разрешите конфликты слияния.");
        }

        if (preview.StagedFiles.Count == 0)
        {
            throw new GitException("В индексе нет подготовленных изменений.");
        }

        await this.VerifySnapshotAsync(preview.Repository, preview.IndexSignature, preview.HeadHash);
        var current = await this.GetCommitPreviewAsync(preview.Repository);
        if (current.HasConflicts
            || !string.Equals(current.IndexSignature, preview.IndexSignature, StringComparison.Ordinal)
            || !string.Equals(current.HeadHash, preview.HeadHash, StringComparison.Ordinal)
            || !current.StagedFiles.SequenceEqual(preview.StagedFiles, StringComparer.Ordinal))
        {
            throw new GitException("Список подготовленных файлов изменился. Обновите окно коммита.");
        }

        var messagePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(messagePath, message, new UTF8Encoding(false));
            await this._git.RunAsync(preview.Repository, "commit", "-F", messagePath);
        }
        finally
        {
            File.Delete(messagePath);
        }
    }

    public async Task<SelectedCommitPreview> GetSelectedCommitPreviewAsync(string repository, IReadOnlyList<ChangedFile> selected)
    {
        if (selected.Count == 0)
        {
            throw new GitException("Выберите хотя бы один файл для коммита.");
        }

        var root = await this._git.GetRepositoryRootAsync(repository);
        await this.EnsureNoOperationInProgressAsync(root);
        var status = await this._git.GetStatusAsync(root, true);
        var hasConflicts = status.Any(file => GitIndexService.IsConflict(file.Status));
        if (hasConflicts)
        {
            throw new GitException("Сначала разрешите конфликты слияния.");
        }

        var indexSignature = await this.GetIndexSignatureAsync(root);
        var headHash = await this.GetHeadHashAsync(root);
        var files = new List<SelectedCommitFile>(selected.Count);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in selected)
        {
            GitIndexService.ValidatePath(file.Path);
            if (file.OriginalPath is not null)
            {
                GitIndexService.ValidatePath(file.OriginalPath);
            }

            if (!seenPaths.Add(file.Path))
            {
                throw new GitException("В выбранном списке есть повторяющиеся файлы.");
            }

            GitIndexService.EnsureStageableFile(root, file.Path);
            if (file.OriginalPath is not null)
            {
                GitIndexService.EnsureStageableFile(root, file.OriginalPath);
            }

            var current = status.FirstOrDefault(item => item.Path == file.Path && item.OriginalPath == file.OriginalPath);
            if (current is null)
            {
                throw new GitException($"Файл «{file.DisplayPath}» больше не изменён. Обновите список.");
            }

            if (await this.IsSubmoduleAsync(root, file))
            {
                throw new GitException("Коммит подмодулей пока не поддерживается. Выберите отдельные файлы.");
            }

            var fingerprint = await GitIndexService.GetWorkingFileFingerprintAsync(root, file);
            files.Add(new SelectedCommitFile(file, current.Status, fingerprint));
        }

        await this.VerifySnapshotAsync(root, indexSignature, headHash);
        var preview = new SelectedCommitPreview(root, files, indexSignature, headHash, false);
        await this.VerifySelectedWorkingFilesAsync(preview, false, indexSignature);

        return preview;
    }

    public async Task CommitSelectedAsync(SelectedCommitPreview preview, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Contains('\0'))
        {
            throw new GitException("Введите сообщение коммита.");
        }

        if (preview.HasConflicts || preview.Files.Count == 0)
        {
            throw new GitException(preview.HasConflicts ? "Сначала разрешите конфликты слияния." : "Выберите хотя бы один файл для коммита.");
        }

        var current = await this.GetSelectedCommitPreviewAsync(preview.Repository, preview.Files.Select(file => file.File).ToArray());
        if (!string.Equals(current.IndexSignature, preview.IndexSignature, StringComparison.Ordinal)
            || !string.Equals(current.HeadHash, preview.HeadHash, StringComparison.Ordinal)
            || current.Files.Count != preview.Files.Count
            || current.Files.Zip(preview.Files).Any(pair => pair.First.Status != pair.Second.Status
                || !string.Equals(pair.First.WorkingFileFingerprint, pair.Second.WorkingFileFingerprint, StringComparison.Ordinal)))
        {
            throw new GitException("Выбранные изменения обновились. Обновите список перед коммитом.");
        }

        var untracked = preview.Files.Where(file => file.Status == "??").ToArray();
        string? intentIndexSignature = null;
        try
        {
            if (untracked.Length > 0)
            {
                var indexEntries = await this.GetIndexEntriesAsync(preview.Repository);
                var intentArguments = new List<string> { "add", "-N", "--" };
                intentArguments.AddRange(untracked.Select(file => GitIndexService.LiteralPathspec(file.File.Path)));
                await this._git.RunAsync(preview.Repository, [.. intentArguments]);
                intentIndexSignature = await this.GetIndexSignatureAsync(preview.Repository);
                await this.VerifyIntentIndexAsync(preview.Repository, indexEntries, untracked);
            }

            var messagePath = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(messagePath, message, new UTF8Encoding(false));
                var arguments = new List<string> { "commit", "--only", "-F", messagePath, "--" };
                foreach (var file in preview.Files)
                {
                    if (file.File.OriginalPath is not null)
                    {
                        arguments.Add(GitIndexService.LiteralPathspec(file.File.OriginalPath));
                    }

                    arguments.Add(GitIndexService.LiteralPathspec(file.File.Path));
                }

                await this.VerifySelectedWorkingFilesAsync(preview, untracked.Length > 0,
                    intentIndexSignature ?? preview.IndexSignature);
                await this._git.RunAsync(preview.Repository, [.. arguments]);
            }
            finally
            {
                File.Delete(messagePath);
            }
        }
        catch
        {
            if (intentIndexSignature is not null && untracked.Length > 0
                && string.Equals(await this.GetIndexSignatureAsync(preview.Repository), intentIndexSignature, StringComparison.Ordinal))
            {
                var arguments = new List<string> { "rm", "--cached", "--ignore-unmatch", "--" };
                arguments.AddRange(untracked.Select(file => GitIndexService.LiteralPathspec(file.File.Path)));
                await this._git.RunAsync(preview.Repository, [.. arguments]);
            }

            throw;
        }
    }

    private async Task VerifySelectedWorkingFilesAsync(
        SelectedCommitPreview preview,
        bool allowIntentToAdd,
        string expectedIndexSignature)
    {
        await this.VerifySnapshotAsync(preview.Repository, expectedIndexSignature, preview.HeadHash);
        await this.EnsureNoOperationInProgressAsync(preview.Repository);
        var status = await this._git.GetStatusAsync(preview.Repository, true);
        if (status.Any(file => GitIndexService.IsConflict(file.Status)))
        {
            throw new GitException("Сначала разрешите конфликты слияния.");
        }

        foreach (var selected in preview.Files)
        {
            var current = status.FirstOrDefault(file => file.Path == selected.File.Path && file.OriginalPath == selected.File.OriginalPath);
            var statusMatches = current is not null && (current.Status == selected.Status
                || allowIntentToAdd && selected.Status == "??" && current.Status == " A");
            if (!statusMatches
                || !string.Equals(await GitIndexService.GetWorkingFileFingerprintAsync(preview.Repository, selected.File),
                    selected.WorkingFileFingerprint, StringComparison.Ordinal))
            {
                throw new GitException($"Файл «{selected.File.DisplayPath}» изменился. Обновите список.");
            }
        }
    }

    private async Task EnsureNoOperationInProgressAsync(string repository)
    {
        var markers = new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "rebase-merge", "rebase-apply", "sequencer" };
        foreach (var marker in markers)
        {
            var markerPath = (await this._git.RunAsync(repository, "rev-parse", "--git-path", marker)).TrimEnd('\r', '\n');
            var fullPath = Path.GetFullPath(Path.Combine(repository, markerPath));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new GitException("Сначала завершите или отмените текущую операцию Git (merge, cherry-pick, rebase и т. п.).");
            }
        }
    }

    private async Task VerifyIntentIndexAsync(
        string repository,
        IReadOnlyList<string> indexEntries,
        IReadOnlyList<SelectedCommitFile> untracked)
    {
        var intentPaths = untracked.Select(file => file.File.Path).ToHashSet(StringComparer.Ordinal);
        var currentEntries = await this.GetIndexEntriesAsync(repository);
        var originalEntries = indexEntries.Where(entry => !GitIndexService.IsPathInSet(entry, intentPaths));
        var addedEntries = currentEntries.Where(entry => GitIndexService.IsPathInSet(entry, intentPaths)).ToArray();
        if (!originalEntries.SequenceEqual(currentEntries.Except(addedEntries), StringComparer.Ordinal)
            || addedEntries.Length != untracked.Count
            || addedEntries.Any(entry => !entry.StartsWith("100", StringComparison.Ordinal)))
        {
            throw new GitException("Индекс изменился во время подготовки выбранных файлов. Обновите список.");
        }
    }

    private async Task<IReadOnlyList<string>> GetIndexEntriesAsync(string repository) =>
        (await this._git.RunAsync(repository, "ls-files", "--stage", "-z"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsPathInSet(string indexEntry, IReadOnlySet<string> paths)
    {
        var separator = indexEntry.IndexOf('\t');
        return separator >= 0 && paths.Contains(indexEntry[(separator + 1)..]);
    }

    private async Task<bool> IsSubmoduleAsync(string repository, ChangedFile file)
    {
        var output = await this._git.RunAsync(repository, "ls-files", "--stage", "-z", "--",
            GitIndexService.LiteralPathspec(file.Path));
        if (output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
        {
            return true;
        }

        if (file.OriginalPath is null)
        {
            return false;
        }

        output = await this._git.RunAsync(repository, "ls-files", "--stage", "-z", "--",
            GitIndexService.LiteralPathspec(file.OriginalPath));
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal));
    }

    private async Task MutateFileAsync(IndexPreview preview, bool stage)
    {
        GitIndexService.ValidatePath(preview.File.Path);
        if (preview.File.OriginalPath is not null)
        {
            GitIndexService.ValidatePath(preview.File.OriginalPath);
        }

        if (stage != (preview.Scope == ChangeScope.WorkingTree))
        {
            throw new GitException("Выбранная область изменений не соответствует действию.");
        }

        await this.VerifySnapshotAsync(preview.Worktree, preview.IndexSignature, preview.HeadHash);
        if (stage)
        {
            GitIndexService.EnsureStageableFile(preview.Worktree, preview.File.Path);
            var currentDiff = await this._changes.GetRevisionAsync(preview.Worktree, preview.File, preview.Scope);
            if (!string.Equals(currentDiff.UnifiedText, preview.Diff.UnifiedText, StringComparison.Ordinal)
                || currentDiff.IsBinary != preview.Diff.IsBinary)
            {
                throw new GitException("Изменения файла обновились. Обновите предпросмотр перед подготовкой файла.");
            }

            var currentFingerprint = await GitIndexService.GetWorkingFileFingerprintAsync(preview.Worktree, preview.File);
            if (!string.Equals(currentFingerprint, preview.WorkingFileFingerprint, StringComparison.Ordinal))
            {
                throw new GitException("Содержимое файла обновилось. Обновите предпросмотр перед подготовкой файла.");
            }

            await this.VerifySnapshotAsync(preview.Worktree, preview.IndexSignature, preview.HeadHash);
        }

        var file = preview.File;
        var paths = file.OriginalPath is null ? new[] { file.Path } : new[] { file.OriginalPath, file.Path };
        var arguments = new List<string>();

        if (stage)
        {
            arguments.AddRange(["add", "--"]);
            arguments.AddRange(paths.Select(GitIndexService.LiteralPathspec));
        }
        else if (preview.HeadHash.Length > 0)
        {
            arguments.AddRange(["restore", "--staged", "--"]);
            arguments.AddRange(paths.Select(GitIndexService.LiteralPathspec));
        }
        else
        {
            arguments.AddRange(["rm", "--cached", "--ignore-unmatch", "--"]);
            arguments.AddRange(paths.Select(GitIndexService.LiteralPathspec));
        }

        await this._git.RunAsync(preview.Worktree, [.. arguments]);
    }

    private async Task ApplyHunkAsync(IndexPreview preview, int hunkIndex, bool reverse)
    {
        if (reverse != (preview.Scope == ChangeScope.Staged))
        {
            throw new GitException("Выбранная область изменений не соответствует действию.");
        }

        if (preview.HunkUnavailableReason is not null || hunkIndex < 0 || hunkIndex >= preview.HunkPatches.Count)
        {
            throw new GitException(preview.HunkUnavailableReason ?? "Выберите существующий блок изменений.");
        }

        if (preview.Diff.IsBinary || preview.Diff.UnifiedText.Contains('\uFFFD'))
        {
            throw new GitException("Для этого файла нельзя безопасно подготовить отдельный блок.");
        }

        await this.VerifySnapshotAsync(preview.Worktree, preview.IndexSignature, preview.HeadHash);
        var current = await this._changes.GetRevisionAsync(preview.Worktree, preview.File, preview.Scope);
        if (!string.Equals(current.UnifiedText, preview.Diff.UnifiedText, StringComparison.Ordinal))
        {
            throw new GitException("Изменения файла обновились. Выберите блок ещё раз.");
        }

        var patchPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(patchPath, preview.HunkPatches[hunkIndex], new UTF8Encoding(false));
            var checkArguments = reverse
                ? new[] { "apply", "--cached", "--reverse", "--check", "--whitespace=nowarn", patchPath }
                : new[] { "apply", "--cached", "--check", "--whitespace=nowarn", patchPath };
            await this._git.RunAsync(preview.Worktree, checkArguments);
            await this.VerifySnapshotAsync(preview.Worktree, preview.IndexSignature, preview.HeadHash);
            var applyArguments = reverse
                ? new[] { "apply", "--cached", "--reverse", "--whitespace=nowarn", patchPath }
                : new[] { "apply", "--cached", "--whitespace=nowarn", patchPath };
            await this._git.RunAsync(preview.Worktree, applyArguments);
        }
        finally
        {
            File.Delete(patchPath);
        }
    }

    private async Task VerifySnapshotAsync(string worktree, string expectedIndex, string expectedHead)
    {
        if (!string.Equals(await this.GetIndexSignatureAsync(worktree), expectedIndex, StringComparison.Ordinal)
            || !string.Equals(await this.GetHeadHashAsync(worktree), expectedHead, StringComparison.Ordinal))
        {
            throw new GitException("Индекс или текущая ветка изменились. Обновите изменения и повторите действие.");
        }
    }

    private async Task<string> GetIndexSignatureAsync(string worktree) =>
        await this._git.RunAsync(worktree, "ls-files", "--stage", "-z");

    private static async Task<string> GetWorkingFileFingerprintAsync(string worktree, ChangedFile file)
    {
        var root = Path.GetFullPath(worktree);
        var paths = file.OriginalPath is null ? new[] { file.Path } : new[] { file.OriginalPath, file.Path };
        var fingerprint = new StringBuilder();
        foreach (var relativePath in paths)
        {
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitException("Путь к файлу выходит за пределы рабочей копии.");
            }

            var pathFingerprint = await GitIndexService.GetPathFingerprintAsync(root, path);
            fingerprint.Append(relativePath.Length).Append(':').Append(relativePath).Append(':')
                .Append(pathFingerprint.Length).Append(':').Append(pathFingerprint).Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())));
    }

    private static async Task<string> GetPathFingerprintAsync(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var currentPath = root;
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (FileNotFoundException)
            {
                var linkTarget = GitIndexService.GetLinkTarget(currentPath);
                if (linkTarget is not null)
                {
                    return "reparse:" + linkTarget;
                }

                return "missing";
            }
            catch (DirectoryNotFoundException)
            {
                var linkTarget = GitIndexService.GetLinkTarget(currentPath);
                if (linkTarget is not null)
                {
                    return "reparse:" + linkTarget;
                }

                return "missing";
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "reparse:" + (GitIndexService.GetLinkTarget(currentPath) ?? "unknown");
            }
        }

        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
        {
            return "directory";
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64_000, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream);
        return "file:" + Convert.ToHexString(digest);
    }

    private static string? GetLinkTarget(string path) => new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;

    private static void EnsureStageableFile(string worktree, string relativePath)
    {
        var root = Path.GetFullPath(worktree);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Путь к файлу выходит за пределы рабочей копии.");
        }

        var currentPath = root;
        var segments = Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            try
            {
                var attributes = File.GetAttributes(currentPath);
                var isLastSegment = index == segments.Length - 1;
                if ((attributes & FileAttributes.Directory) != 0 && isLastSegment
                    || (attributes & FileAttributes.ReparsePoint) != 0 && !isLastSegment)
                {
                    throw new GitException("Папки и подмодули целиком пока не поддерживаются. Выберите отдельный файл внутри папки.");
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

    private async Task<string> GetHeadHashAsync(string worktree)
    {
        try
        {
            return (await this._git.RunAsync(worktree, "rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"))
                .TrimEnd('\r', '\n');
        }
        catch (GitException)
        {
            return "";
        }
    }

    private static IReadOnlyList<string> GetHunkPatches(string patch, out string? reason)
    {
        reason = null;
        if (patch.Length == 0)
        {
            reason = "Для этого файла нет текстовых изменений в выбранной области.";
            return [];
        }

        if (patch.Length > 500_000 || patch.Contains('\uFFFD'))
        {
            reason = "Diff обрезан или его кодировка не позволяет безопасно применить отдельный блок.";
            return [];
        }

        var lines = patch.Split('\n');
        if (patch.Contains("GIT binary patch", StringComparison.Ordinal)
            || patch.Contains("Binary files ", StringComparison.Ordinal)
            || lines.Count(line => line.StartsWith("diff --git ", StringComparison.Ordinal)) != 1
            || lines.Any(line => line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal)
                || line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal)))
        {
            reason = "Бинарные файлы, переименования и изменения режима нельзя подготовить по частям.";
            return [];
        }

        var headerEnd = Array.FindIndex(lines, line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (headerEnd < 0 || !lines.Take(headerEnd).Any(line => line.StartsWith("--- ", StringComparison.Ordinal))
            || !lines.Take(headerEnd).Any(line => line.StartsWith("+++ ", StringComparison.Ordinal)))
        {
            reason = "Git не вернул обычный текстовый diff для подготовки блока.";
            return [];
        }

        var starts = lines.Select((line, index) => (line, index))
            .Where(item => item.line.StartsWith("@@ ", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        var result = new List<string>(starts.Length);
        for (var index = 0; index < starts.Length; index++)
        {
            var end = index + 1 < starts.Length ? starts[index + 1] : lines.Length;
            var hunk = string.Join('\n', lines[..headerEnd].Concat(lines[starts[index]..end]));
            if (!GitIndexService.IsCompleteHunk(lines[starts[index]..end]))
            {
                reason = "Git вернул неполный блок diff.";
                return [];
            }

            result.Add(hunk.EndsWith('\n') ? hunk : hunk + "\n");
        }

        if (result.Count == 0)
        {
            reason = "В diff нет применимых текстовых блоков.";
        }

        return result;
    }

    private static bool IsCompleteHunk(string[] lines)
    {
        if (lines.Length == 0 || !lines[0].StartsWith("@@ ", StringComparison.Ordinal))
        {
            return false;
        }

        return lines.Skip(1).All(line => line.Length == 0 || line[0] is ' ' or '+' or '-' or '\\');
    }

    private static string LiteralPathspec(string path) => ":(literal)" + path;

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\0')
            || path.Split(['/', '\\'], StringSplitOptions.None).Any(segment => segment is ".." or "." or ""))
        {
            throw new GitException("Путь файла некорректен для безопасного обращения к Git.");
        }
    }

    private static bool IsConflict(string status) => status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";
}
