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

            var nestedPath = ".worktrees/task";
            await git.RunAsync(root, "worktree", "add", "-b", "nested-task", nestedPath);
            var nestedFile = (await git.GetStatusAsync(root, true)).Single(file => file.Path == nestedPath + "/");
            var indexBeforeNestedAttempt = await git.RunAsync(root, "ls-files", "--stage");
            var nestedPreview = await index.GetSelectedCommitPreviewAsync(root, [nestedFile]);

            check(nestedPreview.Files.Single().File.Path == nestedPath
                && await git.RunAsync(root, "ls-files", "--stage") == indexBeforeNestedAttempt,
                "Вложенный worktree доступен для выбранного коммита, предпросмотр не меняет индекс");

            await git.RunAsync(root, "add", "--", nestedPath);
            var nestedIndex = await git.RunAsync(root, "ls-files", "--stage", "--", nestedPath);
            var stagedNested = (await git.GetStatusAsync(root, true)).Single(file => file.Path == nestedPath);
            var stagedNestedPreview = await index.GetSelectedCommitPreviewAsync(root, [stagedNested]);

            check(nestedIndex.StartsWith("160000 ", StringComparison.Ordinal) && stagedNestedPreview.Files.Count == 1
                && await git.RunAsync(root, "ls-files", "--stage", "--", nestedPath) == nestedIndex,
                "Подготовленный обычным Git gitlink также доступен для выбранного коммита");

            await git.RunAsync(root, "rm", "--cached", "--", nestedPath);

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
            check((await git.RunAsync(root, "ls-tree", "HEAD", "--", nestedPath)).Length == 0
                && File.Exists(Path.Combine(root, nestedPath, ".git")),
                "Коммит обычных файлов не захватывает вложенный worktree и сохраняет его на диске");
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

            await SelectedCommitChecks.CheckGitLinksAsync(root, nestedPath, git, index, check);

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
            await SelectedCommitChecks.CheckLargePathspecCommitAsync(root, git, index, check);
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

    private static async Task CheckLargePathspecCommitAsync(
        string temporaryRoot,
        GitService git,
        GitIndexService index,
        Action<bool, string> check)
    {
        var repository = Path.Combine(temporaryRoot, "large-pathspec-repository");
        Directory.CreateDirectory(repository);
        await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(repository, "config", "user.name", "Selected Commit Checks");
        await git.RunAsync(repository, "config", "user.email", "selected-commit@example.invalid");
        await git.RunAsync(repository, "config", "core.autocrlf", "false");
        await WriteAsync(repository, "staged-only.txt", "baseline staged file\n");
        await git.RunAsync(repository, "add", "--all");
        await git.RunAsync(repository, "commit", "-m", "large pathspec baseline");
        await WriteAsync(repository, "staged-only.txt", "keep this selected-external staged change\n");
        await git.RunAsync(repository, "add", "--", "staged-only.txt");

        var paths = Enumerable.Range(0, 1_000)
            .Select(index => $"long-files/файл [{index:D4}] сегмент-сегмент-сегмент.txt")
            .ToArray();
        foreach (var path in paths)
        {
            await WriteAsync(repository, path, $"contents {Path.GetFileName(path)}\n");
        }

        var aggregatedPathspecLength = paths.Sum(path => path.Length + ":(literal)".Length + 1);
        check(aggregatedPathspecLength > 32_767,
            "Регрессионная fixture превышает лимит длины агрегированной командной строки Windows");

        var hookFolder = Path.Combine(temporaryRoot, "hooks-for-large-check");
        Directory.CreateDirectory(hookFolder);
        await File.WriteAllTextAsync(Path.Combine(hookFolder, "pre-commit"), "#!/bin/sh\nexit 1\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "config", "core.hooksPath", hookFolder);
        var failedPreview = await index.GetSelectedCommitPreviewAsync(
            repository,
            (await git.GetStatusAsync(repository, true)).Where(file => file.Path.StartsWith("long-files/", StringComparison.Ordinal)).ToArray());
        var hookFailed = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(failedPreview, "large hook failure"));
        var failedStatus = await git.GetStatusAsync(repository, true);
        var failedIndex = await git.RunAsync(repository, "ls-files", "--stage", "--", "long-files");
        check(hookFailed && failedIndex.Length == 0
            && failedStatus.Count(file => file.Path.StartsWith("long-files/", StringComparison.Ordinal) && file.Status == "??") == paths.Length
            && (await git.RunAsync(repository, "show", ":staged-only.txt")).Contains("keep this selected-external staged change", StringComparison.Ordinal),
            "Сбой hook откатывает intent-to-add у 1000 файлов и сохраняет независимое staged-изменение");
        await git.RunAsync(repository, "config", "--unset", "core.hooksPath");

        var preview = await index.GetSelectedCommitPreviewAsync(
            repository,
            (await git.GetStatusAsync(repository, true)).Where(file => file.Path.StartsWith("long-files/", StringComparison.Ordinal)).ToArray());
        await index.CommitSelectedAsync(preview, "large selected commit");
        var committedPaths = (await git.RunAsync(repository, "ls-tree", "-r", "--name-only", "-z", "HEAD", "--", "long-files"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var remainingStatus = await git.GetStatusAsync(repository, true);
        check(committedPaths.SequenceEqual(paths.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            && (await git.RunAsync(repository, "rev-list", "--count", "HEAD")).Trim() == "2"
            && (await git.RunAsync(repository, "show", $"HEAD:{paths[0]}")).Contains("contents", StringComparison.Ordinal)
            && remainingStatus.Count == 1 && remainingStatus[0].Path == "staged-only.txt" && remainingStatus[0].Status == "M "
            && (await git.RunAsync(repository, "show", ":staged-only.txt")).Contains("keep this selected-external staged change", StringComparison.Ordinal),
            "1000 файлов с пробелами, кириллицей и [] коммитятся по pathspec-файлу, сохраняя другой staged-файл");
    }

    private static async Task CheckGitLinksAsync(
        string root,
        string nestedPath,
        GitService git,
        GitIndexService index,
        Action<bool, string> check)
    {
        var nestedRoot = Path.Combine(root, nestedPath);
        const string markerPath = ".worktrees/.artifacts/build-marker";
        await WriteAsync(root, markerPath, "marker\n");
        await WriteAsync(nestedRoot, "uncommitted.txt", "keep nested file\n");
        await WriteAsync(nestedRoot, "selected.txt", "nested working changes\n");
        var selected = (await git.GetStatusAsync(root, true))
            .Where(file => file.Path.TrimEnd('/') == nestedPath || file.Path == markerPath).ToArray();
        var indexBefore = await git.RunAsync(root, "ls-files", "--stage");
        var preview = await index.GetSelectedCommitPreviewAsync(root, selected);
        var hookFolder = Path.Combine(root, ".git", "gitlink-test-hooks");
        Directory.CreateDirectory(hookFolder);
        await File.WriteAllTextAsync(Path.Combine(hookFolder, "pre-commit"), "#!/bin/sh\nexit 1\n", new UTF8Encoding(false));
        await git.RunAsync(root, "config", "core.hooksPath", hookFolder);
        try
        {
            var rejected = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(preview, "hook failure"));
            check(rejected && await git.RunAsync(root, "ls-files", "--stage") == indexBefore
                && (await git.GetStatusAsync(root, true)).Any(file => file.Path == nestedPath + "/" && file.Status == "??"),
                "Сбой hook откатывает временные записи gitlink и обычного файла, сохраняя чужой staged index");
        }
        finally
        {
            await git.RunAsync(root, "config", "--unset", "core.hooksPath");
        }

        preview = await index.GetSelectedCommitPreviewAsync(root, selected);
        await git.RunAsync(nestedRoot, "commit", "--allow-empty", "-m", "advance nested HEAD");
        var staleRejected = await SelectedCommitChecks.RejectedAsync(() => index.CommitSelectedAsync(preview, "stale nested HEAD"));
        check(staleRejected && await git.RunAsync(root, "ls-files", "--stage") == indexBefore,
            "Изменение HEAD вложенной копии после предпросмотра блокирует устаревший коммит без изменения индекса");

        var nestedHead = (await git.RunAsync(nestedRoot, "rev-parse", "HEAD")).Trim();
        await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(root, selected), "add nested worktree and marker");
        var treeEntry = await git.RunAsync(root, "ls-tree", "-r", "HEAD", "--", nestedPath);
        check(treeEntry.Trim() == $"160000 commit {nestedHead}\t{nestedPath}"
            && (await git.RunAsync(root, "show", $"HEAD:{markerPath}")).Contains("marker", StringComparison.Ordinal)
            && File.Exists(Path.Combine(nestedRoot, "uncommitted.txt"))
            && (await git.RunAsync(nestedRoot, "status", "--porcelain")).Contains("selected.txt", StringComparison.Ordinal)
            && (await git.GetStatusAsync(root, true)).Any(file => file.Path == "staged-only.txt" && file.Status == "M "),
            "Выбранный коммит сохраняет gitlink с точным HEAD и обычный файл, не копирует внутренние файлы и сохраняет чужой index");

        await git.RunAsync(nestedRoot, "commit", "--allow-empty", "-m", "advance tracked gitlink");
        nestedHead = (await git.RunAsync(nestedRoot, "rev-parse", "HEAD")).Trim();
        await git.RunAsync(root, "add", "--", nestedPath);
        var tracked = (await git.GetStatusAsync(root, true)).Single(file => file.Path == nestedPath);
        await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(root, [tracked]), "update tracked gitlink");
        check((await git.RunAsync(root, "ls-tree", "HEAD", "--", nestedPath)).Contains(nestedHead, StringComparison.Ordinal),
            "Существующий подготовленный gitlink можно обновить до нового HEAD вложенной копии");

        await git.RemoveWorktreeAsync(root, nestedRoot, allowUncommittedChanges: true);
        var deleted = (await git.GetStatusAsync(root, true)).Single(file => file.Path == nestedPath);
        await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(root, [deleted]), "delete gitlink");
        check((await git.RunAsync(root, "ls-tree", "HEAD", "--", nestedPath)).Length == 0,
            "Удаление вложенной копии можно зафиксировать как удаление gitlink");

        const string independentPath = "nested [repository]";
        var independentRoot = Path.Combine(root, independentPath);
        Directory.CreateDirectory(independentRoot);
        await git.RunAsync(independentRoot, "init", "--initial-branch=main");
        var emptyRepository = (await git.GetStatusAsync(root, true)).Single(file => file.Path == independentPath + "/");
        var beforeEmptyAttempt = await git.RunAsync(root, "ls-files", "--stage");
        var emptyRejected = await SelectedCommitChecks.RejectedAsync(
            () => index.GetSelectedCommitPreviewAsync(root, [emptyRepository]));
        check(emptyRejected && await git.RunAsync(root, "ls-files", "--stage") == beforeEmptyAttempt,
            "Вложенный репозиторий без первого коммита отклоняется без изменения индекса");

        await git.RunAsync(independentRoot, "config", "user.name", "Nested Repository Checks");
        await git.RunAsync(independentRoot, "config", "user.email", "nested@example.invalid");
        await WriteAsync(independentRoot, "inner.txt", "independent repo\n");
        await git.RunAsync(independentRoot, "add", "--all");
        await git.RunAsync(independentRoot, "commit", "-m", "independent root");
        var independentHead = (await git.RunAsync(independentRoot, "rev-parse", "HEAD")).Trim();
        var independent = (await git.GetStatusAsync(root, true)).Single(file => file.Path == independentPath + "/");
        await index.CommitSelectedAsync(await index.GetSelectedCommitPreviewAsync(root, [independent]), "add independent repository");
        check((await git.RunAsync(root, "ls-tree", "-r", "HEAD", "--", independentPath)).Trim()
            == $"160000 commit {independentHead}\t{independentPath}",
            "Вложенный самостоятельный репозиторий с .git-каталогом и glob-путём коммитится как gitlink");
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
