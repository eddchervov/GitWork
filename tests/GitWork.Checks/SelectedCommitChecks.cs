using System.Text;
using GitWork.Core;

internal static class SelectedCommitChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-selected-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        var index = new GitIndexService(git);
        try
        {
            await git.RunAsync(root, "init", "-q");
            await git.RunAsync(root, "config", "user.name", "Selected Commit Checks");
            await git.RunAsync(root, "config", "user.email", "selected-commit@example.invalid");
            await git.RunAsync(root, "config", "core.autocrlf", "false");
            await WriteAsync(root, "selected.txt", "base\n");
            await WriteAsync(root, "staged-only.txt", "base\n");
            await WriteAsync(root, "rename old [1].txt", "rename base\n");
            await WriteAsync(root, "deleted.txt", "delete me\n");
            await git.RunAsync(root, "add", "--all");
            await git.RunAsync(root, "commit", "-m", "baseline");

            await WriteAsync(root, "selected.txt", "partially staged\n");
            await git.RunAsync(root, "add", "--", "selected.txt");
            await WriteAsync(root, "selected.txt", "complete working copy\n");
            await WriteAsync(root, "staged-only.txt", "keep staged\n");
            await git.RunAsync(root, "add", "--", "staged-only.txt");
            await git.RunAsync(root, "mv", "--", "rename old [1].txt", "rename new [1].txt");
            await git.RunAsync(root, "rm", "--", "deleted.txt");

            var changed = await git.GetStatusAsync(root, true);
            var selectedFile = changed.Single(file => file.Path == "selected.txt");
            var mergeHead = Path.Combine(root, ".git", "MERGE_HEAD");
            await File.WriteAllTextAsync(mergeHead, "in-progress\n", new UTF8Encoding(false));
            var activeOperationRejected = await SelectedCommitChecks.RejectedAsync(
                () => index.GetSelectedCommitPreviewAsync(root, [selectedFile]));
            File.Delete(mergeHead);
            check(activeOperationRejected, "Выбранный коммит блокируется во время незавершённого merge");

            var combined = await new GitChangeService(git).GetWorkingRevisionAsync(root, selectedFile);
            check(combined.OldText == "base" && combined.NewText == "complete working copy",
                "Единый diff сравнивает HEAD с рабочим файлом, включая staged и unstaged части");

            var rename = changed.Single(file => file.OriginalPath is not null);
            var delete = changed.Single(file => file.Path == "deleted.txt");
            await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(root, [selectedFile, rename, delete]), "selected files\nподробности");
            var selectedContent = await git.RunAsync(root, "show", "HEAD:selected.txt");
            var keptStatus = await git.GetStatusAsync(root, true);
            check(selectedContent.Contains("complete working copy", StringComparison.Ordinal)
                && keptStatus.Any(file => file.Path == "staged-only.txt" && file.Status == "M ")
                && !keptStatus.Any(file => file.Path == "selected.txt" || file.Path == "deleted.txt" || file.OriginalPath == "rename old [1].txt"),
                "Коммит включает выбранные целые файлы и сохраняет подготовленный невыбранный файл");

            check((await git.RunAsync(root, "show", "-s", "--format=%B", "HEAD")).Contains("подробности", StringComparison.Ordinal),
                "Сообщение коммита сохраняет UTF-8 описание");
            var deletedPathIsAbsent = await SelectedCommitChecks.RejectedAsync(() => git.RunAsync(root, "cat-file", "-e", "HEAD:deleted.txt"));
            check((await git.RunAsync(root, "show", "HEAD:rename new [1].txt")).Contains("rename base", StringComparison.Ordinal)
                && deletedPathIsAbsent,
                "Выбранный коммит поддерживает переименование и удаление");

            var untrackedPath = "new [glob].txt";
            await WriteAsync(root, untrackedPath, "untracked contents\n");
            var untracked = (await git.GetStatusAsync(root, true)).Single(file => file.Path == untrackedPath);
            await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(root, [untracked]), "untracked selected");
            check((await git.RunAsync(root, "show", $"HEAD:{untrackedPath}")).Contains("untracked contents", StringComparison.Ordinal),
                "Новый файл с glob-символами коммитится как буквальный путь");

            var unborn = Path.Combine(root, "unborn");
            Directory.CreateDirectory(unborn);
            await git.RunAsync(unborn, "init", "-q");
            await git.RunAsync(unborn, "config", "user.name", "Selected Commit Checks");
            await git.RunAsync(unborn, "config", "user.email", "selected-commit@example.invalid");
            await WriteAsync(unborn, "first.txt", "first root commit\n");
            await WriteAsync(unborn, "leave-untracked.txt", "leave outside root commit\n");
            var unbornStatus = await git.GetStatusAsync(unborn, true);
            var unbornSelected = unbornStatus.Single(file => file.Path == "first.txt");
            var unbornDiff = await new GitChangeService(git).GetWorkingRevisionAsync(unborn, unbornSelected);
            await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(unborn, [unbornSelected]), "initial selected commit");
            check(unbornDiff.OldText.Length == 0 && unbornDiff.NewText.Contains("first root commit", StringComparison.Ordinal)
                && (await git.GetStatusAsync(unborn, true)).Single().Path == "leave-untracked.txt",
                "Выбранный файл можно коммитить в пустом репозитории, сохраняя остальные новые файлы");

            await WriteAsync(root, "selected.txt", "stale baseline\n");
            var staleFile = (await git.GetStatusAsync(root, true)).Single(file => file.Path == "selected.txt");
            var staleFilePreview = await index.GetSelectedCommitPreviewAsync(root, [staleFile]);
            await WriteAsync(root, "selected.txt", "changed after preview\n");
            var staleFileRejected = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(staleFilePreview, "stale file"));
            check(staleFileRejected, "Изменённый после предпросмотра файл отклоняется до коммита");

            await WriteAsync(root, "selected.txt", "index stale baseline\n");
            var indexStaleFile = (await git.GetStatusAsync(root, true)).Single(file => file.Path == "selected.txt");
            var indexStalePreview = await index.GetSelectedCommitPreviewAsync(root, [indexStaleFile]);
            await git.RunAsync(root, "add", "--", "selected.txt");
            var staleIndexRejected = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(indexStalePreview, "stale index"));
            check(staleIndexRejected, "Изменение индекса после предпросмотра отклоняет коммит");

            var headStaleFile = (await git.GetStatusAsync(root, true)).Single(file => file.Path == "selected.txt");
            var headStalePreview = await index.GetSelectedCommitPreviewAsync(root, [headStaleFile]);
            await git.RunAsync(root, "commit", "--allow-empty", "-m", "advance HEAD");
            var staleHeadRejected = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(headStalePreview, "stale HEAD"));
            check(staleHeadRejected, "Изменившийся после предпросмотра HEAD отклоняет коммит");

            await WriteAsync(root, "selected.txt", "hook baseline\n");
            await git.RunAsync(root, "add", "--", "selected.txt");
            await git.RunAsync(root, "commit", "-m", "hook baseline");
            var hookFolder = Path.Combine(root, "hooks-for-check");
            Directory.CreateDirectory(hookFolder);
            var hookPath = Path.Combine(hookFolder, "pre-commit");
            await File.WriteAllTextAsync(hookPath, "#!/bin/sh\nexit 1\n", new UTF8Encoding(false));
            await git.RunAsync(root, "config", "core.hooksPath", hookFolder);
            var hookUntracked = "hook [failure].txt";
            await WriteAsync(root, hookUntracked, "must remain\n");
            var hookFile = (await git.GetStatusAsync(root, true)).Single(file => file.Path == hookUntracked);
            var hookPreview = await index.GetSelectedCommitPreviewAsync(root, [hookFile]);
            var hookFailed = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(hookPreview, "must fail"));
            var afterHook = (await git.GetStatusAsync(root, true)).Single(file => file.Path == hookUntracked);
            check(hookFailed && afterHook.Status == "??" && await File.ReadAllTextAsync(Path.Combine(root, hookUntracked)) == "must remain\n",
                "Сбой pre-commit сохраняет файл и очищает временную intent-to-add запись");

            await git.RunAsync(root, "config", "--unset", "core.hooksPath");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var temporaryPath = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(temporaryPath, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("GitWork-selected-commit-", StringComparison.Ordinal)
                && Directory.Exists(fullRoot))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories).Prepend(fullRoot))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(fullRoot, true);
            }
        }
    }

    private static async Task WriteAsync(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
    }

    private static async Task<bool> RejectedAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return false;
        }
        catch (GitException)
        {
            return true;
        }
    }
}
