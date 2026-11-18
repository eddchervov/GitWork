using System.Text;
using GitWork.Core;

internal static class StashChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-stash-checks-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var peer = Path.Combine(root, "peer");
        var conflictWorktree = Path.Combine(root, "conflict");
        var git = new GitService();
        var service = new GitStashService(git);
        Directory.CreateDirectory(root);

        try
        {
            await StashChecks.RunSha256Async(root, git, check);
            Directory.CreateDirectory(repository);
            await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
            await git.RunAsync(repository, "config", "user.name", "Git Work Stash Checks");
            await git.RunAsync(repository, "config", "user.email", "stash-checks@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "ignored.log\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "base\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--all");
            await git.RunAsync(repository, "commit", "-m", "base");
            await git.RunAsync(repository, "worktree", "add", "--detach", "--", peer, "HEAD");

            var nestedRepository = Path.Combine(repository, "nested");
            Directory.CreateDirectory(nestedRepository);
            await git.RunAsync(nestedRepository, "init", "--initial-branch=main", "-q");
            await File.WriteAllTextAsync(Path.Combine(nestedRepository, "nested.txt"), "nested repository", new UTF8Encoding(false));
            var nestedStatusBefore = await git.GetStatusAsync(repository, includeAllUntrackedFiles: true);
            var nestedStashRejected = false;
            try
            {
                await service.CreateAsync(repository, "nested only", includeUntracked: true);
            }
            catch (GitException)
            {
                var nestedStatusAfter = await git.GetStatusAsync(repository, includeAllUntrackedFiles: true);
                nestedStashRejected = File.Exists(Path.Combine(nestedRepository, "nested.txt"))
                    && (await service.GetAsync(repository)).Count == 0
                    && nestedStatusBefore.SequenceEqual(nestedStatusAfter);
            }

            check(nestedStashRejected, "Вложенный Git-репозиторий не объявляется сохранённым, если stash -u его пропустил");
            Directory.Delete(nestedRepository, true);

            var trackedPath = Path.Combine(repository, "tracked.txt");
            await File.WriteAllTextAsync(trackedPath, "staged one\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--", "tracked.txt");
            await File.WriteAllTextAsync(trackedPath, "unstaged one\n", new UTF8Encoding(false));
            var untrackedPath = Path.Combine(repository, "untracked.txt");
            var ignoredPath = Path.Combine(repository, "ignored.log");
            await File.WriteAllTextAsync(untrackedPath, "not included yet\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(ignoredPath, "ignored content\n", new UTF8Encoding(false));
            var rebaseMarker = (await git.RunAsync(repository, "rev-parse", "--git-path", "REBASE_HEAD")).TrimEnd('\r', '\n');
            if (!Path.IsPathRooted(rebaseMarker))
            {
                rebaseMarker = Path.GetFullPath(Path.Combine(repository, rebaseMarker));
            }

            await File.WriteAllTextAsync(rebaseMarker, "test marker", new UTF8Encoding(false));
            var operationBlocked = false;
            try
            {
                await service.CreateAsync(repository, "blocked during rebase", includeUntracked: true);
            }
            catch (GitException)
            {
                operationBlocked = await File.ReadAllTextAsync(trackedPath) == "unstaged one\n" && File.Exists(untrackedPath);
            }

            File.Delete(rebaseMarker);
            check(operationBlocked, "Создание stash отклоняется во время незавершённой операции Git без изменения файлов");

            await service.CreateAsync(repository, "tracked only", includeUntracked: false);
            var trackedOnly = (await service.GetAsync(repository)).Single();
            var peerEntries = await service.GetAsync(peer);
            var firstStatus = await git.GetStatusAsync(repository, includeAllUntrackedFiles: true);
            check(peerEntries.Any(item => item.Hash == trackedOnly.Hash) && File.Exists(untrackedPath)
                && File.Exists(ignoredPath) && firstStatus.Count == 1 && firstStatus[0].Path == "untracked.txt",
                "Stash общий между worktree, хранит staged и unstaged, не забирает untracked без выбора и не трогает ignored");

            var trackedOnlyFiles = await service.GetFilesAsync(peer, trackedOnly);
            check(trackedOnlyFiles.Count == 1 && trackedOnlyFiles[0].Path == "tracked.txt",
                "Просмотр состава stash доступен из другого worktree и не включает незахваченный untracked-файл");
            var trackedDiff = await service.GetDiffAsync(peer, trackedOnly, trackedOnlyFiles[0]);
            check(trackedDiff.OldText.Contains("base", StringComparison.Ordinal)
                && trackedDiff.NewText.Contains("unstaged one", StringComparison.Ordinal)
                && trackedDiff.LoadContextAsync is not null,
                "Предпросмотр stash показывает tracked diff и поддерживает догрузку контекста без чтения рабочей копии");
            var stagedFiles = await service.GetFilesAsync(peer, trackedOnly, ChangeScope.Staged);
            var stagedDiff = await service.GetDiffAsync(peer, trackedOnly, stagedFiles[0], ChangeScope.Staged);
            check(stagedFiles.Count == 1 && stagedDiff.NewText.Contains("staged one", StringComparison.Ordinal)
                && !stagedDiff.NewText.Contains("unstaged one", StringComparison.Ordinal),
                "Режим индекса показывает только stash^1 → stash^2 и сохраняет догрузку контекста");

            var dirtyTargetPath = Path.Combine(peer, "tracked.txt");
            var originalTargetContent = await File.ReadAllTextAsync(dirtyTargetPath);
            await File.WriteAllTextAsync(dirtyTargetPath, "keep this tracked change\n", new UTF8Encoding(false));
            var dirtyApplyRejected = false;
            try
            {
                await service.ApplyAsync(peer, trackedOnly, removeAfterApply: true);
            }
            catch (GitException)
            {
                dirtyApplyRejected = File.Exists(dirtyTargetPath)
                    && (await service.GetAsync(repository)).Any(item => item.Hash == trackedOnly.Hash);
            }

            check(dirtyApplyRejected, "Apply отклоняет грязную рабочую копию, не удаляя её файлы и stash");
            await File.WriteAllTextAsync(dirtyTargetPath, originalTargetContent, new UTF8Encoding(false));

            File.Delete(untrackedPath);
            await File.WriteAllTextAsync(trackedPath, "staged two\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--", "tracked.txt");
            await File.WriteAllTextAsync(trackedPath, "unstaged two\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(untrackedPath, "saved new file\n", new UTF8Encoding(false));
            await service.CreateAsync(repository, "with new file", includeUntracked: true);
            var entries = await service.GetAsync(repository);
            var withUntracked = entries[0];
            var savedFiles = await service.GetFilesAsync(repository, withUntracked);
            var newFile = savedFiles.SingleOrDefault(item => item.Path == "untracked.txt");
            var newFileDiff = newFile is null ? null : await service.GetDiffAsync(repository, withUntracked, newFile);
            check(entries.Count == 2 && !File.Exists(untrackedPath) && File.Exists(ignoredPath)
                && newFile is not null && newFileDiff?.NewText.Contains("saved new file", StringComparison.Ordinal) == true,
                "Включённый untracked попадает в stash и diff из третьего родителя, ignored файл остаётся на месте");

            var unrelatedUntrackedPath = Path.Combine(peer, "nested", "worktree", "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(unrelatedUntrackedPath)!);
            await File.WriteAllTextAsync(unrelatedUntrackedPath, "keep unrelated file\n", new UTF8Encoding(false));
            await service.ApplyAsync(peer, withUntracked, removeAfterApply: false);
            var appliedStatus = await git.GetStatusAsync(peer, includeAllUntrackedFiles: true);
            var appliedTracked = appliedStatus.SingleOrDefault(item => item.Path == "tracked.txt");
            check(appliedTracked?.Status == "MM" && File.ReadAllText(Path.Combine(peer, "tracked.txt")).Contains("unstaged two", StringComparison.Ordinal)
                && File.Exists(Path.Combine(peer, "untracked.txt")) && File.ReadAllText(unrelatedUntrackedPath).Contains("keep unrelated", StringComparison.Ordinal)
                && (await service.GetAsync(repository)).Any(item => item.Hash == withUntracked.Hash),
                "Apply сохраняет посторонние untracked-файлы и восстанавливает staged, unstaged и stash untracked");

            await git.RunAsync(peer, "reset", "--hard", "HEAD");
            await git.RunAsync(peer, "clean", "-fd");
            await File.WriteAllTextAsync(untrackedPath, "keep collision\n", new UTF8Encoding(false));
            var collisionRejected = false;
            try
            {
                await service.ApplyAsync(repository, withUntracked, removeAfterApply: true);
            }
            catch (GitException)
            {
                collisionRejected = File.ReadAllText(untrackedPath).Contains("keep collision", StringComparison.Ordinal)
                    && (await service.GetAsync(repository)).Any(item => item.Hash == withUntracked.Hash);
            }

            check(collisionRejected, "Apply блокирует совпадающий untracked-путь, сохраняя файл пользователя и stash");
            File.Delete(untrackedPath);
            await service.ApplyAsync(repository, withUntracked, removeAfterApply: true);
            var afterPop = await service.GetAsync(repository);
            check(afterPop.Count == 1 && afterPop[0].Hash == trackedOnly.Hash
                && File.Exists(untrackedPath) && File.ReadAllText(untrackedPath).Contains("saved new file", StringComparison.Ordinal),
                "Apply-and-drop удаляет только применённый stash после успешного применения");
            File.Delete(untrackedPath);

            await git.RunAsync(repository, "worktree", "add", "-b", "stash-conflict", "--", conflictWorktree, "HEAD");
            await File.WriteAllTextAsync(trackedPath, "conflicting incoming\n", new UTF8Encoding(false));
            await service.CreateAsync(repository, "conflict source", includeUntracked: false);
            var conflictEntry = (await service.GetAsync(repository)).First();
            var conflictTrackedPath = Path.Combine(conflictWorktree, "tracked.txt");
            await File.WriteAllTextAsync(conflictTrackedPath, "conflicting target\n", new UTF8Encoding(false));
            await git.RunAsync(conflictWorktree, "add", "--", "tracked.txt");
            await git.RunAsync(conflictWorktree, "commit", "-m", "conflicting target");
            var conflictPreserved = false;
            try
            {
                await service.ApplyAsync(conflictWorktree, conflictEntry, removeAfterApply: true);
            }
            catch (GitException)
            {
                conflictPreserved = (await service.GetAsync(repository)).Any(item => item.Hash == conflictEntry.Hash);
            }

            check(conflictPreserved, "Конфликт apply сохраняет выбранный stash даже при запросе pop");
            await git.RunAsync(conflictWorktree, "reset", "--hard", "HEAD");

            var beforeEmpty = await service.GetAsync(repository);
            var emptyRejected = false;
            try
            {
                await service.CreateAsync(repository, "empty", includeUntracked: true);
            }
            catch (GitException)
            {
                emptyRejected = true;
            }

            check(emptyRejected && (await service.GetAsync(repository)).Select(item => item.Hash).SequenceEqual(beforeEmpty.Select(item => item.Hash)),
                "Нет изменений: пустой stash не создаётся и существующие записи не меняются");

            var ignoredTargetPath = Path.Combine(peer, "ignored.log");
            await File.WriteAllTextAsync(ignoredTargetPath, "keep ignored target\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(ignoredPath, "forced tracked stash file\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "-f", "--", "ignored.log");
            await service.CreateAsync(repository, "ignored collision", includeUntracked: false);
            var ignoredCollisionEntry = (await service.GetAsync(repository)).First();
            var ignoredCollisionRejected = false;
            try
            {
                await service.ApplyAsync(peer, ignoredCollisionEntry, removeAfterApply: true);
            }
            catch (GitException)
            {
                ignoredCollisionRejected = await File.ReadAllTextAsync(ignoredTargetPath) == "keep ignored target\n"
                    && (await git.GetStatusAsync(peer)).Count == 0
                    && (await service.GetAsync(repository)).Any(item => item.Hash == ignoredCollisionEntry.Hash);
            }

            check(ignoredCollisionRejected, "Apply сохраняет ignored-файл целевой копии и stash при конфликте пути");
            File.Delete(ignoredTargetPath);
            await service.DropAsync(repository, ignoredCollisionEntry);

            await File.WriteAllTextAsync(trackedPath, "index only\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--", "tracked.txt");
            await File.WriteAllTextAsync(trackedPath, "base\n", new UTF8Encoding(false));
            await service.CreateAsync(repository, "  ", includeUntracked: false);
            var unnamedEntry = (await service.GetAsync(repository)).First();
            var unnamedWorkingFiles = await service.GetFilesAsync(repository, unnamedEntry, ChangeScope.WorkingTree);
            var unnamedStagedFiles = await service.GetFilesAsync(repository, unnamedEntry, ChangeScope.Staged);
            var unnamedDiff = await service.GetDiffAsync(repository, unnamedEntry, unnamedStagedFiles.Single(), ChangeScope.Staged);
            check(unnamedEntry.Message.Length > 0 && unnamedWorkingFiles.Count == 0 && unnamedStagedFiles.Count == 1
                && unnamedDiff.NewText.Contains("index only", StringComparison.Ordinal),
                "Пустое название создаёт stash со стандартным сообщением, а staged-only diff отражает индекс при чистом worktree");
            await service.DropAsync(repository, unnamedEntry);

            var invalidMessageRejected = false;
            try
            {
                await service.CreateAsync(repository, "bad\u0001message", includeUntracked: false);
            }
            catch (GitException)
            {
                invalidMessageRejected = true;
            }

            check(invalidMessageRejected, "Управляющие символы в названии stash отклоняются");

            var previousAuthorDate = Environment.GetEnvironmentVariable("GIT_AUTHOR_DATE");
            var previousCommitterDate = Environment.GetEnvironmentVariable("GIT_COMMITTER_DATE");
            try
            {
                Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", "2001-01-01T00:00:00Z");
                Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", "2001-01-01T00:00:00Z");
                await File.WriteAllTextAsync(trackedPath, "identical stash content\n", new UTF8Encoding(false));
                await git.RunAsync(repository, "add", "--", "tracked.txt");
                await service.CreateAsync(repository, "identical", includeUntracked: false);
                var firstDuplicate = (await service.GetAsync(repository)).First();
                await File.WriteAllTextAsync(trackedPath, "identical stash content\n", new UTF8Encoding(false));
                await git.RunAsync(repository, "add", "--", "tracked.txt");
                await service.CreateAsync(repository, "identical", includeUntracked: false);
                var duplicates = (await service.GetAsync(repository)).Where(item => item.Hash == firstDuplicate.Hash).ToArray();
                check(duplicates.Length == 1 && (await git.GetStatusAsync(repository)).Count == 0,
                    "Повторное сохранение идентичного содержимого успешно и не создаёт лишнюю reflog-запись");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", previousAuthorDate);
                Environment.SetEnvironmentVariable("GIT_COMMITTER_DATE", previousCommitterDate);
            }

            await File.WriteAllTextAsync(trackedPath, "separator subject\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "stash", "push", "--message", "separator \u001f and \u001e subject");
            var separatorEntry = (await service.GetAsync(repository)).First();
            var separatorMessageParsed = separatorEntry.Message == "separator \u001f and \u001e subject";
            await service.DropAsync(repository, separatorEntry);
            check(separatorMessageParsed, "Reflog parsing remains intact when a historical stash subject contains separator characters");

            await File.WriteAllTextAsync(trackedPath, "empty subject source\n", new UTF8Encoding(false));
            await service.CreateAsync(repository, "empty subject source", includeUntracked: false);
            var emptySubjectSource = (await service.GetAsync(repository)).First();
            await git.RunAsync(repository, "stash", "store", "--message=", trackedOnly.Hash);
            var emptySubjectEntry = (await service.GetAsync(repository)).First();
            var emptySubjectParsed = emptySubjectEntry.Message.Length == 0;
            await service.DropAsync(repository, emptySubjectEntry);
            check(emptySubjectParsed, "Пустое поле subject не сдвигает NUL-разбор последующих записей stash");
            await service.DropAsync(repository, emptySubjectSource);

            await service.DropAsync(repository, trackedOnly);
            var remainingAfterDrop = await service.GetAsync(repository);
            var conflictStillPresent = remainingAfterDrop.Any(item => item.Hash == conflictEntry.Hash)
                && remainingAfterDrop.All(item => item.Hash != trackedOnly.Hash);
            var staleRejected = false;
            try
            {
                await service.DropAsync(repository, trackedOnly);
            }
            catch (GitException)
            {
                staleRejected = true;
            }

            check(conflictStillPresent && staleRejected,
                "Удаление использует hash при изменившемся порядке stash и отклоняет устаревшую запись");
        }
        finally
        {
            foreach (var worktree in new[] { conflictWorktree, peer })
            {
                if (!Directory.Exists(worktree))
                {
                    continue;
                }

                try
                {
                    await git.RunAsync(repository, "worktree", "remove", "--force", "--", worktree);
                }
                catch (GitException exception)
                {
                    Console.Error.WriteLine(exception.Message);
                }
            }

            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                    }

                    Directory.Delete(root, true);
                }
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine(exception.Message);
            }
        }
    }

    private static async Task RunSha256Async(string root, GitService git, Action<bool, string> check)
    {
        var repository = Path.Combine(root, "sha256");
        Directory.CreateDirectory(repository);
        try
        {
            await git.RunAsync(repository, "init", "--object-format=sha256", "--initial-branch=main", "-q");
        }
        catch (GitException exception) when (exception.Message.Contains("sha256", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("object-format", StringComparison.OrdinalIgnoreCase))
        {
            check(true, "Git не поддерживает SHA256 object format; проверка stash SHA256 пропущена");
            return;
        }

        await git.RunAsync(repository, "config", "user.name", "Git Work SHA256 Checks");
        await git.RunAsync(repository, "config", "user.email", "stash-sha256@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "base\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--all");
        await git.RunAsync(repository, "commit", "-m", "base");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "changed\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "new.txt"), "new sha256 file\n", new UTF8Encoding(false));
        var service = new GitStashService(git);
        await service.CreateAsync(repository, "sha256 stash", includeUntracked: true);
        var entry = (await service.GetAsync(repository)).Single();
        var files = await service.GetFilesAsync(repository, entry);
        var newFile = files.Single(file => file.Path == "new.txt");
        var diff = await service.GetDiffAsync(repository, entry, newFile);
        await service.ApplyAsync(repository, entry, removeAfterApply: false);
        check(entry.Hash.Length == 64 && diff.NewText.Contains("new sha256 file", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(repository, "tracked.txt")).Contains("changed", StringComparison.Ordinal)
            && File.Exists(Path.Combine(repository, "new.txt")),
            "Stash list, untracked preview и apply работают в репозитории SHA256");
    }
}
