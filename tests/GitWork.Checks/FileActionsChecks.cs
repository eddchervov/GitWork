using System.Text;
using GitWork.Core;

internal static class FileActionsChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-file-actions-checks-" + Guid.NewGuid().ToString("N"));
        var ignoreRepository = Path.Combine(root, "ignore-repository");
        var repository = Path.Combine(root, "repository");
        var bulkRepository = Path.Combine(root, "bulk-repository");
        var overlapRepository = Path.Combine(root, "overlap-repository");
        var unbornRepository = Path.Combine(root, "unborn-repository");
        var backupRoot = Path.Combine(root, "backups");
        var git = new GitService();
        var service = new GitFileActionsService(git);
        Directory.CreateDirectory(ignoreRepository);
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(bulkRepository);
        Directory.CreateDirectory(overlapRepository);
        Directory.CreateDirectory(unbornRepository);

        try
        {
            await FileActionsChecks.CheckIgnoreRulesAsync(ignoreRepository, service, git, check);
            await FileActionsChecks.CheckDiscardAsync(repository, unbornRepository, backupRoot, service, git, check);
            await FileActionsChecks.CheckDiscardAllAsync(bulkRepository, Path.Combine(root, "bulk-backups"), service, git, check);
            await FileActionsChecks.CheckDiscardOverlappingPathsAsync(overlapRepository, Path.Combine(root, "overlap-backups"), service, git, check);
        }
        finally
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
    }

    private static async Task CheckIgnoreRulesAsync(
        string repository,
        GitFileActionsService service,
        GitService git,
        Action<bool, string> check)
    {
        await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(repository, "config", "user.name", "Git Work File Actions Checks");
        await git.RunAsync(repository, "config", "user.email", "file-actions@example.invalid");
        var ignorePath = Path.Combine(repository, ".gitignore");
        var initialBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# existing\r\n")).ToArray();
        await File.WriteAllBytesAsync(ignorePath, initialBytes);

        var specialPath = "# ![literal].txt";
        var specialRule = await service.AddIgnoreRuleAsync(repository, specialPath, directory: false);
        var bytesAfterSpecial = await File.ReadAllBytesAsync(ignorePath);
        await service.AddIgnoreRuleAsync(repository, specialPath, directory: false);
        var bytesAfterDuplicate = await File.ReadAllBytesAsync(ignorePath);
        var ignoreText = Encoding.UTF8.GetString(bytesAfterSpecial);
        check(specialRule == "/\\#\\ \\!\\[literal\\].txt"
            && bytesAfterSpecial.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())
            && ignoreText.StartsWith("\uFEFF# existing\r\n", StringComparison.Ordinal)
            && ignoreText.Contains(specialRule + "\r\n", StringComparison.Ordinal)
            && !ignoreText.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n')
            && bytesAfterSpecial.SequenceEqual(bytesAfterDuplicate),
            "Правило .gitignore экранирует #, !, скобки и пробелы, сохраняет BOM/CRLF и не дублируется");

        var rootFile = Path.Combine(repository, "anchor.log");
        var nestedDirectory = Path.Combine(repository, "nested");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(rootFile, "root");
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "anchor.log"), "nested");
        await service.AddIgnoreRuleAsync(repository, "anchor.log", directory: false);
        await service.AddIgnoreRuleAsync(repository, "generated", directory: true);
        Directory.CreateDirectory(Path.Combine(repository, "generated"));
        await File.WriteAllTextAsync(Path.Combine(repository, "generated", "cache.bin"), "ignored");
        var anchoredAtRoot = await FileActionsChecks.IsIgnoredAsync(git, repository, "anchor.log");
        var nestedSameNameIsNotIgnored = await FileActionsChecks.IsIgnoredAsync(git, repository, "nested/anchor.log");
        var folderContentsIgnored = await FileActionsChecks.IsIgnoredAsync(git, repository, "generated/cache.bin");
        var fileRule = await service.AddIgnoreRuleAsync(repository, "root-only", directory: false);
        var folderRule = await service.AddIgnoreRuleAsync(repository, "folder-only", directory: true);
        check(anchoredAtRoot && !nestedSameNameIsNotIgnored && folderContentsIgnored
            && fileRule == "/root-only" && folderRule == "/folder-only/",
            "Правила привязаны к корню, а правило папки завершается слешем");

        await File.WriteAllTextAsync(ignorePath, "# lf\n", new UTF8Encoding(false));
        await service.AddIgnoreRuleAsync(repository, "lf-rule", directory: false);
        var lfText = await File.ReadAllTextAsync(ignorePath, Encoding.UTF8);
        check(lfText.Contains("# lf\n/lf-rule\n", StringComparison.Ordinal) && !lfText.Contains('\r'),
            "Добавление правила сохраняет переводы строк LF");

        var invalidBytes = new byte[] { (byte)'#', (byte)' ', (byte)'b', (byte)'a', (byte)'d', (byte)'\n', 0xFF, 0xFE };
        await File.WriteAllBytesAsync(ignorePath, invalidBytes);
        var invalidEncodingRejected = false;
        try
        {
            await service.AddIgnoreRuleAsync(repository, "safe.txt", directory: false);
        }
        catch (GitException)
        {
            invalidEncodingRejected = (await File.ReadAllBytesAsync(ignorePath)).SequenceEqual(invalidBytes);
        }

        check(invalidEncodingRejected, "Некорректный UTF-8 в .gitignore отклоняется без перезаписи исходных байтов");
    }

    private static async Task CheckDiscardAsync(
        string repository,
        string unbornRepository,
        string backupRoot,
        GitFileActionsService service,
        GitService git,
        Action<bool, string> check)
    {
        await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(repository, "config", "user.name", "Git Work File Actions Checks");
        await git.RunAsync(repository, "config", "user.email", "file-actions@example.invalid");
        await git.RunAsync(repository, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "base tracked\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "other.txt"), "base other\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "rename.txt"), "rename base\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "delete.txt"), "delete base\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--all");
        await git.RunAsync(repository, "commit", "-m", "base");

        var trackedPath = Path.Combine(repository, "tracked.txt");
        await File.WriteAllTextAsync(trackedPath, "staged version\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--", "tracked.txt");
        await File.WriteAllTextAsync(trackedPath, "working version\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "other.txt"), "other staged version\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--", "other.txt");
        var trackedChange = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "tracked.txt");
        var trackedPreview = await service.GetDiscardPreviewAsync(repository, trackedChange);
        var trackedBackup = await service.DiscardAsync(trackedPreview, backupRoot);
        var trackedBackupPath = trackedBackup ?? throw new InvalidOperationException("Для tracked-файла должна создаваться резервная копия.");
        var stagedTracked = (await git.RunAsync(repository, "show", ":tracked.txt")).TrimEnd('\r', '\n');
        var stagedOther = (await git.RunAsync(repository, "show", ":other.txt")).TrimEnd('\r', '\n');
        check(await File.ReadAllTextAsync(trackedPath) == "base tracked\n", "Discard восстанавливает рабочую копию из HEAD");
        check(stagedTracked == "base tracked", "Discard восстанавливает индекс выбранного файла из HEAD");
        check(stagedOther == "other staged version", "Discard оставляет staged-изменение другого файла");
        check(await File.ReadAllTextAsync(Path.Combine(trackedBackupPath, "working", "tracked.txt")) == "working version\n",
            "Discard сохраняет рабочую версию выбранного файла");
        check(await File.ReadAllTextAsync(Path.Combine(trackedBackupPath, "index", "tracked.txt")) == "staged version\n",
            "Discard сохраняет индексную версию выбранного файла");

        await File.WriteAllTextAsync(trackedPath, "stale baseline\n", new UTF8Encoding(false));
        var staleFile = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "tracked.txt");
        var stalePreview = await service.GetDiscardPreviewAsync(repository, staleFile);
        await File.WriteAllTextAsync(trackedPath, "changed after preview\n", new UTF8Encoding(false));
        var staleRejected = false;
        try
        {
            await service.DiscardAsync(stalePreview, backupRoot);
        }
        catch (GitException)
        {
            staleRejected = await File.ReadAllTextAsync(trackedPath) == "changed after preview\n";
        }

        check(staleRejected, "Устаревший preview отклоняется без отмены более новых изменений");
        await File.WriteAllTextAsync(trackedPath, "base tracked\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "reset", "--", "tracked.txt");

        var untrackedPath = Path.Combine(repository, "new.txt");
        await File.WriteAllTextAsync(untrackedPath, "untracked contents\n", new UTF8Encoding(false));
        var untracked = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "new.txt");
        var untrackedPreview = await service.GetDiscardPreviewAsync(repository, untracked);
        var untrackedBackup = await service.DiscardAsync(untrackedPreview, backupRoot);
        check(!File.Exists(untrackedPath) && untrackedBackup is not null
            && await File.ReadAllTextAsync(Path.Combine(untrackedBackup, "working", "new.txt")) == "untracked contents\n",
            "Неотслеживаемый файл удаляется только после копирования в backup");

        var renamePath = Path.Combine(repository, "renamed.txt");
        await git.RunAsync(repository, "mv", "--", "rename.txt", "renamed.txt");
        await File.WriteAllTextAsync(renamePath, "renamed working version\n", new UTF8Encoding(false));
        var renamed = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "renamed.txt");
        var renamePreview = await service.GetDiscardPreviewAsync(repository, renamed);
        var renameBackup = await service.DiscardAsync(renamePreview, backupRoot);
        check(File.Exists(Path.Combine(repository, "rename.txt")) && !File.Exists(renamePath)
            && renameBackup is not null
            && await File.ReadAllTextAsync(Path.Combine(renameBackup, "working", "renamed.txt")) == "renamed working version\n",
            "Discard переименования восстанавливает исходный путь и сохраняет новую версию");

        File.Delete(Path.Combine(repository, "delete.txt"));
        var deleted = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "delete.txt");
        var deletedPreview = await service.GetDiscardPreviewAsync(repository, deleted);
        await service.DiscardAsync(deletedPreview, backupRoot);
        check(await File.ReadAllTextAsync(Path.Combine(repository, "delete.txt")) == "delete base\n",
            "Discard удалённого файла восстанавливает версию из HEAD");

        await File.WriteAllTextAsync(Path.Combine(repository, "staged-new.txt"), "staged new version\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--", "staged-new.txt");
        await File.WriteAllTextAsync(Path.Combine(repository, "staged-new.txt"), "working new version\n", new UTF8Encoding(false));
        var stagedNew = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "staged-new.txt");
        var stagedNewPreview = await service.GetDiscardPreviewAsync(repository, stagedNew);
        var stagedNewBackup = await service.DiscardAsync(stagedNewPreview, backupRoot);
        check(!File.Exists(Path.Combine(repository, "staged-new.txt"))
            && stagedNewBackup is not null
            && await File.ReadAllTextAsync(Path.Combine(stagedNewBackup, "working", "staged-new.txt")) == "working new version\n"
            && await File.ReadAllTextAsync(Path.Combine(stagedNewBackup, "index", "staged-new.txt")) == "staged new version\n",
            "Staged-new файл удаляется после сохранения index и working копий");

        await git.RunAsync(unbornRepository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(unbornRepository, "config", "user.name", "Git Work File Actions Checks");
        await git.RunAsync(unbornRepository, "config", "user.email", "file-actions@example.invalid");
        await git.RunAsync(unbornRepository, "config", "core.autocrlf", "false");
        var unbornFile = Path.Combine(unbornRepository, "unborn.txt");
        await File.WriteAllTextAsync(unbornFile, "unborn index version\n", new UTF8Encoding(false));
        await git.RunAsync(unbornRepository, "add", "--", "unborn.txt");
        await File.WriteAllTextAsync(unbornFile, "unborn working version\n", new UTF8Encoding(false));
        var unbornChange = (await git.GetStatusAsync(unbornRepository, includeAllUntrackedFiles: true)).Single();
        var unbornPreview = await service.GetDiscardPreviewAsync(unbornRepository, unbornChange);
        var unbornBackup = await service.DiscardAsync(unbornPreview, backupRoot);
        var unbornIndex = await git.RunAsync(unbornRepository, "ls-files", "--stage", "--", "unborn.txt");
        check(unbornPreview.HeadHash.Length == 0 && !File.Exists(unbornFile) && unbornIndex.Length == 0,
            "Staged-new файл в unborn-репозитории удаляется из индекса и рабочей копии");
        check(unbornBackup is not null
            && await File.ReadAllTextAsync(Path.Combine(unbornBackup, "working", "unborn.txt")) == "unborn working version\n",
            "Staged-new файл в unborn-репозитории сохраняет рабочую версию в backup");
        check(unbornBackup is not null
            && await File.ReadAllTextAsync(Path.Combine(unbornBackup, "index", "unborn.txt")) == "unborn index version\n",
            "Staged-new файл в unborn-репозитории сохраняет index-версию в backup");

        var directoryRejected = false;
        try
        {
            await service.GetDiscardPreviewAsync(repository, new ChangedFile("??", "ordinary-folder/", null));
        }
        catch (GitException)
        {
            directoryRejected = true;
        }

        var escapeRejected = false;
        try
        {
            await service.GetDiscardPreviewAsync(repository, new ChangedFile("??", "../outside.txt", null));
        }
        catch (GitException)
        {
            escapeRejected = true;
        }

        var fullPathRejected = false;
        try
        {
            GitFileActionsService.GetFullPath(repository, ".git/config");
        }
        catch (GitException)
        {
            fullPathRejected = true;
        }

        check(directoryRejected && escapeRejected && fullPathRejected,
            "Discard и GetFullPath запрещают директории, выходы из репозитория и пути внутрь .git");

        var nestedRepository = Path.Combine(repository, "nested-repository");
        Directory.CreateDirectory(nestedRepository);
        await git.RunAsync(nestedRepository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(nestedRepository, "config", "user.name", "Nested Fixture");
        await git.RunAsync(nestedRepository, "config", "user.email", "nested@example.invalid");
        var nestedFile = Path.Combine(nestedRepository, "nested.txt");
        await File.WriteAllTextAsync(nestedFile, "nested base\n", new UTF8Encoding(false));
        await git.RunAsync(nestedRepository, "add", "--", "nested.txt");
        await git.RunAsync(nestedRepository, "commit", "-m", "nested base");
        await git.RunAsync(repository, "add", "--", "nested-repository");
        await git.RunAsync(repository, "commit", "-m", "add gitlink");
        await File.WriteAllTextAsync(nestedFile, "nested changed\n", new UTF8Encoding(false));
        await git.RunAsync(nestedRepository, "add", "--", "nested.txt");
        await git.RunAsync(nestedRepository, "commit", "-m", "nested change");
        var gitLink = (await git.GetStatusAsync(repository, includeAllUntrackedFiles: true)).Single(file => file.Path == "nested-repository");
        var gitLinkRejected = false;
        try
        {
            await service.GetDiscardPreviewAsync(repository, gitLink);
        }
        catch (GitException)
        {
            gitLinkRejected = gitLink.IsGitLink && (await git.RunAsync(nestedRepository, "rev-parse", "HEAD")).Trim().Length > 0;
        }

        check(gitLinkRejected, "Вложенный Git-репозиторий защищён от отмены как обычного файла");
    }

    private static async Task CheckDiscardAllAsync(
        string repository,
        string backupRoot,
        GitFileActionsService service,
        GitService git,
        Action<bool, string> check)
    {
        await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(repository, "config", "user.name", "Git Work File Actions Checks");
        await git.RunAsync(repository, "config", "user.email", "file-actions@example.invalid");
        await git.RunAsync(repository, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "base tracked\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "staged.txt"), "base staged\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "rename.txt"), "base rename\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "delete.txt"), "base delete\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "/ignored.data\n", new UTF8Encoding(false));

        var nestedRepository = Path.Combine(repository, "nested-repository");
        Directory.CreateDirectory(nestedRepository);
        await git.RunAsync(nestedRepository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(nestedRepository, "config", "user.name", "Nested Fixture");
        await git.RunAsync(nestedRepository, "config", "user.email", "nested@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(nestedRepository, "nested.txt"), "nested base\n", new UTF8Encoding(false));
        await git.RunAsync(nestedRepository, "add", "--", "nested.txt");
        await git.RunAsync(nestedRepository, "commit", "-m", "nested base");
        await git.RunAsync(repository, "add", "--all");
        await git.RunAsync(repository, "commit", "-m", "bulk base");

        var trackedPath = Path.Combine(repository, "tracked.txt");
        await File.WriteAllTextAsync(trackedPath, "staged tracked\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--", "tracked.txt");
        await File.WriteAllTextAsync(trackedPath, "working tracked\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "staged.txt"), "staged only\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--", "staged.txt");
        await git.RunAsync(repository, "mv", "--", "rename.txt", "renamed.txt");
        await File.WriteAllTextAsync(Path.Combine(repository, "renamed.txt"), "working renamed\n", new UTF8Encoding(false));
        File.Delete(Path.Combine(repository, "delete.txt"));
        var untrackedPath = Path.Combine(repository, "untracked.txt");
        await File.WriteAllTextAsync(untrackedPath, "untracked contents\n", new UTF8Encoding(false));
        var nestedPath = Path.Combine(nestedRepository, "nested.txt");
        await File.WriteAllTextAsync(nestedPath, "nested changed\n", new UTF8Encoding(false));
        await git.RunAsync(nestedRepository, "add", "--", "nested.txt");
        await git.RunAsync(nestedRepository, "commit", "-m", "nested change");
        await File.WriteAllTextAsync(Path.Combine(repository, "ignored.data"), "ignored contents\n", new UTF8Encoding(false));
        var folderPath = Path.Combine(repository, "loose-folder");
        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "loose.txt"), "loose folder contents\n", new UTF8Encoding(false));

        var initialStatus = await git.GetStatusAsync(repository, includeAllUntrackedFiles: true);
        var preview = await service.GetDiscardAllPreviewAsync(repository);
        var selectedPaths = preview.Files.Select(file => file.File.Path).ToHashSet(StringComparer.Ordinal);
        check(preview.Files.Count == 6
            && preview.Files.Any(file => file.File.Path == "tracked.txt")
            && preview.Files.Any(file => file.File.Path == "staged.txt")
            && preview.Files.Any(file => file.File.Path == "renamed.txt" && file.File.OriginalPath == "rename.txt")
            && preview.Files.Any(file => file.File.Path == "delete.txt")
            && preview.Files.Any(file => file.File.Path == "untracked.txt")
            && preview.Files.Any(file => file.File.Path == "loose-folder/loose.txt")
            && !selectedPaths.Contains("nested-repository") && !selectedPaths.Contains("ignored.data")
            && initialStatus.Single(file => file.Path == "nested-repository").IsGitLink,
            "Bulk preview включает обычные изменения, но исключает gitlink и ignored-файл");

        await File.WriteAllTextAsync(trackedPath, "newer tracked\n", new UTF8Encoding(false));
        var staleRejected = false;
        try
        {
            await service.DiscardAllAsync(preview, backupRoot);
        }
        catch (GitException)
        {
            staleRejected = await File.ReadAllTextAsync(trackedPath) == "newer tracked\n"
                && await File.ReadAllTextAsync(untrackedPath) == "untracked contents\n";
        }

        check(staleRejected, "Bulk discard отклоняет устаревший snapshot до любых изменений");

        var freshPreview = await service.GetDiscardAllPreviewAsync(repository);
        var backupBlocker = Path.Combine(Path.GetDirectoryName(backupRoot)!, "backup-blocker");
        await File.WriteAllTextAsync(backupBlocker, "not a directory", new UTF8Encoding(false));
        var backupFailedWithoutMutation = false;
        try
        {
            await service.DiscardAllAsync(freshPreview, backupBlocker);
        }
        catch (IOException)
        {
            backupFailedWithoutMutation = await File.ReadAllTextAsync(trackedPath) == "newer tracked\n"
                && await File.ReadAllTextAsync(untrackedPath) == "untracked contents\n"
                && (await git.RunAsync(repository, "show", ":tracked.txt")).TrimEnd('\r', '\n') == "staged tracked";
        }

        check(backupFailedWithoutMutation, "Если создание backup падает, индекс и рабочие файлы не меняются");

        var previewWithLateFile = await service.GetDiscardAllPreviewAsync(repository);
        var lateFile = Path.Combine(repository, "late-untracked.txt");
        await File.WriteAllTextAsync(lateFile, "appeared after preview\n", new UTF8Encoding(false));
        var backup = await service.DiscardAllAsync(previewWithLateFile, backupRoot);
        var backupPath = backup ?? throw new InvalidOperationException("Для bulk discard должна создаваться резервная копия.");
        var stagedTracked = (await git.RunAsync(repository, "show", ":tracked.txt")).TrimEnd('\r', '\n');
        var stagedFile = (await git.RunAsync(repository, "show", ":staged.txt")).TrimEnd('\r', '\n');
        check(await File.ReadAllTextAsync(trackedPath) == "base tracked\n" && stagedTracked == "base tracked"
            && await File.ReadAllTextAsync(Path.Combine(repository, "staged.txt")) == "base staged\n"
            && stagedFile == "base staged"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "working", "tracked.txt")) == "newer tracked\n"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "index", "tracked.txt")) == "staged tracked\n"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "working", "staged.txt")) == "staged only\n"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "index", "staged.txt")) == "staged only\n",
            "Bulk discard сохраняет staged/working файлы и восстанавливает их к HEAD");

        check(File.Exists(Path.Combine(repository, "rename.txt")) && !File.Exists(Path.Combine(repository, "renamed.txt"))
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "working", "renamed.txt")) == "working renamed\n"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "index", "renamed.txt")) == "base rename\n"
            && File.Exists(Path.Combine(repository, "delete.txt"))
            && !File.Exists(Path.Combine(backupPath, "working", "delete.txt"))
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "index", "delete.txt")) == "base delete\n",
            "Bulk backup и восстановление сохраняют rename и delete в отдельных состояниях");

        check(!File.Exists(untrackedPath)
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "working", "untracked.txt")) == "untracked contents\n"
            && !File.Exists(Path.Combine(folderPath, "loose.txt")) && Directory.Exists(folderPath)
            && File.Exists(lateFile)
            && File.Exists(Path.Combine(repository, "ignored.data"))
            && File.Exists(nestedPath)
            && (await git.RunAsync(nestedRepository, "show", "HEAD:nested.txt")).TrimEnd('\r', '\n') == "nested changed",
            "Bulk discard удаляет новые файлы после backup, оставляет новые после preview, ignored и nested repo");

        File.Delete(lateFile);
        var emptyPreviewRejected = false;
        try
        {
            await service.GetDiscardAllPreviewAsync(repository);
        }
        catch (GitException)
        {
            emptyPreviewRejected = true;
        }

        check(emptyPreviewRejected, "Bulk preview отклоняет выбор без обычных изменённых файлов");
    }

    private static async Task CheckDiscardOverlappingPathsAsync(
        string repository,
        string backupRoot,
        GitFileActionsService service,
        GitService git,
        Action<bool, string> check)
    {
        await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(repository, "config", "user.name", "Git Work File Actions Checks");
        await git.RunAsync(repository, "config", "user.email", "file-actions@example.invalid");
        await git.RunAsync(repository, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repository, "old.txt"), "HEAD rename content\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "deleted.txt"), "HEAD delete content\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--all");
        await git.RunAsync(repository, "commit", "-m", "overlap base");

        await git.RunAsync(repository, "mv", "--", "old.txt", "new.txt");
        await File.WriteAllTextAsync(Path.Combine(repository, "old.txt"), "untracked replacement for old\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "rm", "--", "deleted.txt");
        await File.WriteAllTextAsync(Path.Combine(repository, "deleted.txt"), "untracked replacement for deleted\n", new UTF8Encoding(false));

        var preview = await service.GetDiscardAllPreviewAsync(repository);
        var rename = preview.Files.Single(file => file.File.Path == "new.txt");
        var oldReplacement = preview.Files.Single(file => file.File.Path == "old.txt");
        var deletedReplacement = preview.Files.Single(file => file.File.Path == "deleted.txt");
        check(rename.File.OriginalPath == "old.txt" && oldReplacement.Status == "??"
            && deletedReplacement.File.Path == "deleted.txt",
            "Bulk snapshot содержит staged rename и новые файлы, совпадающие с исходными путями");

        var backup = await service.DiscardAllAsync(preview, backupRoot);
        var backupPath = backup ?? throw new InvalidOperationException("Для пересекающихся файлов должна создаваться резервная копия.");
        var statusAfterDiscard = await git.GetStatusAsync(repository, includeAllUntrackedFiles: true);
        check(statusAfterDiscard.Count == 0
            && await File.ReadAllTextAsync(Path.Combine(repository, "old.txt")) == "HEAD rename content\n"
            && !File.Exists(Path.Combine(repository, "new.txt"))
            && await File.ReadAllTextAsync(Path.Combine(repository, "deleted.txt")) == "HEAD delete content\n",
            "Bulk discard сохраняет HEAD-файлы после staged rename и staged deletion с совпадающими untracked путями");
        check(await File.ReadAllTextAsync(Path.Combine(backupPath, "working", "old.txt")) == "untracked replacement for old\n"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "working", "deleted.txt")) == "untracked replacement for deleted\n"
            && await File.ReadAllTextAsync(Path.Combine(backupPath, "index", "new.txt")) == "HEAD rename content\n",
            "Backup сохраняет untracked замены в working и переименованную index-версию");
    }

    private static async Task<bool> IsIgnoredAsync(GitService git, string repository, string relativePath)
    {
        try
        {
            await git.RunAsync(repository, "check-ignore", "-q", "--", relativePath);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }
}
