using System.Text;
using GitWork.Core;

public static class IndexChecks
{
    public static async Task RunAsync(string source, Action<bool, string> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "GitWork-index-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var git = new GitService();
            var bare = Path.Combine(temporaryRoot, "source.git");
            var worktree = Path.Combine(temporaryRoot, "worktree");
            await git.RunAsync(temporaryRoot, "clone", "--bare", "--shared", "--", Path.GetFullPath(source), bare);
            var branches = await git.GetBranchesAsync(bare, await git.GetWorktreesAsync(bare));
            var sourceBranch = branches.First(branch => !branch.IsRemote);
            await git.RunAsync(bare, "worktree", "add", "--detach", "--", worktree, sourceBranch.FullName);
            await git.RunAsync(worktree, "config", "core.autocrlf", "false");

            var trackedPaths = (await git.RunAsync(worktree, "ls-files", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var candidate = trackedPaths.Select(path => new
                {
                    Path = path,
                    FullPath = Path.Combine(worktree, path.Replace('/', Path.DirectorySeparatorChar))
                })
                .Where(item => File.Exists(item.FullPath) && new FileInfo(item.FullPath).Length < 100_000)
                .Select(item => (item.Path, item.FullPath, Bytes: File.ReadAllBytes(item.FullPath)))
                .First(item => !item.Bytes.Contains((byte)0)
                    && new UTF8Encoding(false, true).GetString(item.Bytes).Split('\n').Length >= 20);
            var original = new UTF8Encoding(false, true).GetString(candidate.Bytes);
            var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            lines[2] += " index-check-first";
            lines[^3] += " index-check-second";
            var changed = string.Join('\n', lines);
            await File.WriteAllTextAsync(candidate.FullPath, changed, new UTF8Encoding(false));
            var changedFile = new ChangedFile(" M", candidate.Path, null);
            var index = new GitIndexService(git);
            var workingPreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.WorkingTree);
            check(workingPreview.HunkPatches.Count >= 2, "Отдельные блоки определяются по заголовкам unified diff");

            await index.StageHunkAsync(workingPreview, 0);
            check((await File.ReadAllTextAsync(candidate.FullPath)) == changed, "Подготовка блока не меняет рабочий файл");
            var staged = await new GitChangeService(git).GetRevisionAsync(worktree, changedFile, ChangeScope.Staged);
            var unstaged = await new GitChangeService(git).GetRevisionAsync(worktree, changedFile, ChangeScope.WorkingTree);
            check(staged.UnifiedText.Contains("index-check-first", StringComparison.Ordinal)
                && !staged.UnifiedText.Contains("index-check-second", StringComparison.Ordinal)
                && unstaged.UnifiedText.Contains("index-check-second", StringComparison.Ordinal),
                "Подготовка одного блока оставляет остальные изменения в рабочем слое");

            var stagedPreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.Staged);
            await index.UnstageHunkAsync(stagedPreview, 0);
            check((await git.RunAsync(worktree, "diff", "--cached", "--name-only", "-z")).Length == 0
                && (await File.ReadAllTextAsync(candidate.FullPath)) == changed,
                "Снятие блока из индекса сохраняет содержимое рабочего файла");

            await git.RunAsync(worktree, "config", "core.autocrlf", "false");
            var crlfBaseline = changed.Replace("\n", "\r\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(candidate.FullPath, crlfBaseline, new UTF8Encoding(false));
            workingPreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.WorkingTree);
            await index.StageFileAsync(workingPreview);
            var crlfLines = crlfBaseline.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            crlfLines[2] += " index-check-crlf-hunk";
            var crlfChanged = string.Join("\r\n", crlfLines);
            await File.WriteAllTextAsync(candidate.FullPath, crlfChanged, new UTF8Encoding(false));
            workingPreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.WorkingTree);
            await index.StageHunkAsync(workingPreview, 0);
            var stagedCrlf = await git.RunAsync(worktree, "show", ":" + candidate.Path);
            check(stagedCrlf.Contains("\r\n", StringComparison.Ordinal)
                && stagedCrlf.Contains("index-check-crlf-hunk\r\n", StringComparison.Ordinal),
                "Подготовка блока сохраняет CRLF в содержимом индекса");
            stagedPreview = await index.GetPreviewAsync(worktree, changedFile with { Status = "M " }, ChangeScope.Staged);
            await index.UnstageHunkAsync(stagedPreview, 0);
            check((await File.ReadAllTextAsync(candidate.FullPath)) == crlfChanged,
                "Снятие блока CRLF не меняет рабочий файл");
            stagedPreview = await index.GetPreviewAsync(worktree, changedFile with { Status = "M " }, ChangeScope.Staged);
            await index.UnstageFileAsync(stagedPreview);
            check((await git.GetStatusAsync(worktree)).Any(file => file.Path == candidate.Path && file.Status.EndsWith('M'))
                && (await File.ReadAllTextAsync(candidate.FullPath)) == crlfChanged,
                "Подготовка и снятие файла сохраняют рабочие изменения");

            var staleWorkingPreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.WorkingTree);
            await File.AppendAllTextAsync(candidate.FullPath, "\r\nindex-check-stale-working\r\n");
            var staleWorkingRejected = false;
            try
            {
                await index.StageFileAsync(staleWorkingPreview);
            }
            catch (GitException)
            {
                staleWorkingRejected = true;
            }

            check(staleWorkingRejected && (await git.GetStatusAsync(worktree)).Any(file => file.Path == candidate.Path && file.Status.EndsWith('M')),
                "Изменившийся рабочий diff отклоняет устаревшую подготовку файла");

            const string untrackedPath = "index-check-[stale].txt";
            var untrackedFullPath = Path.Combine(worktree, untrackedPath);
            await File.WriteAllTextAsync(untrackedFullPath, "До предпросмотра.\n", new UTF8Encoding(false));
            var untrackedFile = (await git.GetStatusAsync(worktree, true)).Single(file => file.Path == untrackedPath);
            var untrackedPreview = await index.GetPreviewAsync(worktree, untrackedFile, ChangeScope.WorkingTree);
            await File.WriteAllTextAsync(untrackedFullPath, "После предпросмотра.\n", new UTF8Encoding(false));
            var staleUntrackedRejected = false;
            try
            {
                await index.StageFileAsync(untrackedPreview);
            }
            catch (GitException)
            {
                staleUntrackedRejected = true;
            }

            check(staleUntrackedRejected && !(await git.RunAsync(worktree, "diff", "--cached", "--name-only", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Contains(untrackedPath, StringComparer.Ordinal),
                "Изменившийся untracked diff отклоняет подготовку файла");

            const string binaryPath = "index-check-stale.bin";
            var binaryFullPath = Path.Combine(worktree, binaryPath);
            await File.WriteAllBytesAsync(binaryFullPath, [0, 1, 2, 3]);
            var binaryFile = (await git.GetStatusAsync(worktree, true)).Single(file => file.Path == binaryPath);
            var binaryPreview = await index.GetPreviewAsync(worktree, binaryFile, ChangeScope.WorkingTree);
            await File.WriteAllBytesAsync(binaryFullPath, [0, 1, 2, 4]);
            var staleBinaryRejected = false;
            try
            {
                await index.StageFileAsync(binaryPreview);
            }
            catch (GitException)
            {
                staleBinaryRejected = true;
            }

            check(staleBinaryRejected && !(await git.RunAsync(worktree, "diff", "--cached", "--name-only", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Contains(binaryPath, StringComparer.Ordinal),
                "Изменившееся бинарное содержимое отклоняет устаревшую подготовку файла");

            const string untrackedDirectory = "index-check-directory";
            Directory.CreateDirectory(Path.Combine(worktree, untrackedDirectory));
            await File.WriteAllTextAsync(Path.Combine(worktree, untrackedDirectory, "one.txt"), "Первый файл.\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(worktree, untrackedDirectory, "two.txt"), "Второй файл.\n", new UTF8Encoding(false));
            var directoryPreview = await index.GetPreviewAsync(
                worktree,
                new ChangedFile("??", untrackedDirectory, null),
                ChangeScope.WorkingTree);
            var directoryRejected = false;
            try
            {
                await index.StageFileAsync(directoryPreview);
            }
            catch (GitException)
            {
                directoryRejected = true;
            }

            check(directoryRejected && !(await git.RunAsync(worktree, "diff", "--cached", "--name-only", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(path => path.StartsWith(untrackedDirectory + "/", StringComparison.Ordinal)),
                "Подготовка папки не захватывает вложенные файлы автоматически");

            var largeContent = string.Join('\n', Enumerable.Range(0, 80_000).Select(line => $"index-check-large-{line:D5}"));
            await File.WriteAllTextAsync(candidate.FullPath, largeContent, new UTF8Encoding(false));
            var largePreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.WorkingTree);
            check(largePreview.Diff.Message?.Contains("обрезан", StringComparison.OrdinalIgnoreCase) == true,
                "Большой diff показывает ограничение предпросмотра");
            await File.AppendAllTextAsync(candidate.FullPath, "\nindex-check-hidden-tail-changed\n");
            var staleTruncatedRejected = false;
            try
            {
                await index.StageFileAsync(largePreview);
            }
            catch (GitException)
            {
                staleTruncatedRejected = true;
            }

            check(staleTruncatedRejected, "Fingerprint обнаруживает изменение за обрезанной частью diff");

            var stalePreview = await index.GetPreviewAsync(worktree, changedFile, ChangeScope.WorkingTree);
            var secondPath = trackedPaths.First(path => path != candidate.Path);
            var secondFullPath = Path.Combine(worktree, secondPath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(secondFullPath, "\nindex-check-stale\n");
            await git.RunAsync(worktree, "add", "--", ":(literal)" + secondPath);
            var staleRejected = false;
            try
            {
                await index.StageFileAsync(stalePreview);
            }
            catch (GitException)
            {
                staleRejected = true;
            }

            check(staleRejected, "Изменившийся индекс отклоняет устаревший предпросмотр");
            var commitPreview = await index.GetCommitPreviewAsync(worktree);
            var emptyMessageRejected = false;
            try
            {
                await index.CommitAsync(commitPreview, "  ");
            }
            catch (GitException)
            {
                emptyMessageRejected = true;
            }

            check(emptyMessageRejected, "Пустое сообщение не запускает коммит");
            await git.RunAsync(worktree, "add", "--", ":(literal)" + candidate.Path);
            var noCommitRun = false;
            try
            {
                await index.CommitAsync(commitPreview, "index-check-dry-run");
            }
            catch (GitException)
            {
                noCommitRun = true;
            }

            check(noCommitRun, "Устаревший индекс отклоняется до запуска коммита");
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(temporaryRoot);
            var tempPath = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolvedRoot.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot).StartsWith("GitWork-index-checks-", StringComparison.Ordinal)
                && Directory.Exists(resolvedRoot))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(resolvedRoot, "*", SearchOption.AllDirectories)
                    .Prepend(resolvedRoot))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(resolvedRoot, true);
            }
        }
    }
}
