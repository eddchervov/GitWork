using GitWork.Checks;
using GitWork.Core;

var checks = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Проверка не пройдена: {name}");
    }

    checks++;
    Console.WriteLine($"OK: {name}");
}

if (args.Contains("--preparation-only", StringComparer.Ordinal))
{
    await PreparationChecks.RunAsync(Check);
    return;
}

var trees = GitOutputParser.ParseWorktrees(
    "worktree C:/Проекты/Основная\0HEAD abc\0branch refs/heads/develop\0\0"
    + "worktree C:/Проекты/С пробелом\0HEAD def\0detached\0locked проверка\0\0"
    + "worktree C:/missing\0HEAD ghi\0branch refs/heads/feature/a\0prunable gitdir missing\0\0");

Check(trees.Count == 3, "Чтение нескольких worktree");
Check(trees[0].IsMain && !trees[1].IsMain, "Определение основной рабочей копии");
Check(trees[0].Branch == "develop" && trees[2].Branch == "feature/a", "Имена веток и префиксы refs");
Check(trees[1].Path == "C:/Проекты/С пробелом", "Кириллица и пробелы в пути");
Check(trees[1].Branch == "" && trees[1].IsLocked && trees[1].Reason == "проверка", "Detached HEAD и блокировка");
Check(trees[2].IsPrunable, "Недоступная рабочая копия");
Check(GitOutputParser.ParseWorktrees("worktree C:/bare\0bare\0\0")[0].IsBare, "Bare-репозиторий");
Check(GitOutputParser.ParseWorktrees("").Count == 0, "Пустой список worktree");
var files = GitOutputParser.ParseStatus(" M путь/файл.cs\0R  новое имя.cs\0старое имя.cs\0?? new.txt\0UU conflict.cs\0");
Check(files.Count == 4 && files[0].Status == " M", "Статусы индекса и рабочей папки");
Check(files[1].Path == "новое имя.cs" && files[1].OriginalPath == "старое имя.cs", "Переименование с NUL-разделителями");
Check(files[2].Status == "??" && files[3].Status == "UU", "Новые файлы и конфликты");
Check(GitOutputParser.ParseStatus("").Count == 0, "Чистая рабочая папка");
Check(GitService.PathsEqual(@"C:\Projects\One\", "c:/Projects/One"), "Сравнение путей Windows");
Check(SearchMatcher.Matches("  DEV online  ", "dev", @"G:\work\Online"), "Поиск по нескольким полям без учёта регистра");
Check(!SearchMatcher.Matches("dev missing", "dev", "Online"), "Все слова поискового запроса должны совпасть");
Check(SearchMatcher.Matches("", "любой репозиторий"), "Пустой поиск показывает все результаты");
var sharedRepository = new RepositoryInfo("Shared", @"C:\work\Shared", "work");
var sampleProjects = new[]
{
    new ProjectGroup { Name = "Online", RepositoryPaths = [sharedRepository.Path] },
    new ProjectGroup { Name = "Private", RepositoryPaths = [sharedRepository.Path] }
};

var projectEntries = RepositoryPresentation.GetEntries([sharedRepository], sampleProjects, RepositoryGrouping.Projects, "");
Check(projectEntries.Count == 2 && projectEntries.Select(entry => entry.ProjectId).Distinct().Count() == 2, "Один репозиторий входит в несколько проектов");
Check(RepositoryPresentation.GetEntries([sharedRepository], sampleProjects, RepositoryGrouping.Projects, "Online").Single().Section == "Online", "Поиск по проекту сохраняет его репозитории");
Check(RepositoryPresentation.GetEntries([sharedRepository], sampleProjects, RepositoryGrouping.All, "Private").Count == 1, "Общий список не дублирует общий репозиторий");
var folderEntries = RepositoryPresentation.GetEntries(
    [sharedRepository, new RepositoryInfo("Other", @"D:\work\Other", "work")], [], RepositoryGrouping.Folders, "");

Check(folderEntries.Select(entry => entry.Section).Distinct().Count() == 2, "Одинаковые имена папок на разных дисках не смешиваются");
var workspaceLayout = new ProjectWorkspace
{
    Name = "Проверка",
    WorktreePaths = new Dictionary<string, string>
    {
        [@"C:\repos\Online"] = @"D:\tasks\51818\Online",
        [@"C:\repos\Shared"] = @"D:\tasks\51818\Shared"
    }
};

Check(workspaceLayout.RootPath == @"D:\tasks\51818", "Общая папка выводится для ранее сохранённого комплекта");
workspaceLayout.WorktreePaths[@"C:\repos\Shared"] = @"E:\separate\Shared";
Check(workspaceLayout.RootPath is null, "Для несоседних копий общая папка не подменяется");
Check(new ProjectWorkspace().RootPath is null, "Пустой комплект не получает выдуманную общую папку");

var temporaryRoot = Path.Combine(Path.GetTempPath(), "GitWork-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);

try
{
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "normal", ".git"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "Черновики", "hidden", ".git"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "node_modules", "hidden", ".git"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "_tmp", "hidden", ".git"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "linked"));
    File.WriteAllText(Path.Combine(temporaryRoot, "linked", ".git"), "gitdir: elsewhere");
    var repositories = RepositoryScanner.Scan(temporaryRoot);
    Check(repositories.Count == 2, "Поиск репозиториев и исключение черновиков и зависимостей");
    Check(repositories.Any(repository => repository.Name == "linked"), "Обнаружение .git-файла worktree");
    Check(RepositoryScanner.Scan(Path.Combine(temporaryRoot, "_tmp", "hidden")).Count == 1,
        "Временный репозиторий исключён из автопоиска, но доступен при явном выборе его пути");
    var git = new GitService();
    await git.RunAsync(temporaryRoot, "init", "--initial-branch=main");
    File.WriteAllText(Path.Combine(temporaryRoot, "проверка.txt"), "Тест Git status.");
    var actualChanges = await git.GetStatusAsync(temporaryRoot);
    Check(actualChanges.Any(file => file.Path == "проверка.txt" && file.Status == "??"), "Чтение настоящего Git status без коммитов");
    var failedSafely = false;

    try
    {
        await git.CreateWorktreeAsync(temporaryRoot, Path.Combine(temporaryRoot, "normal"), "example", null);
    }
    catch (GitException)
    {
        failedSafely = true;
    }

    Check(failedSafely && Directory.Exists(Path.Combine(temporaryRoot, "normal", ".git")), "Существующая папка защищена от перезаписи");
    failedSafely = false;

    try
    {
        await git.SwitchBranchAsync(temporaryRoot, "--orphan");
    }
    catch (GitException)
    {
        failedSafely = true;
    }

    Check(failedSafely, "Имя ветки не может подменить аргументы Git");

    if (args.Length > 0)
    {
        var barePath = Path.Combine(temporaryRoot, "seed.git");
        var worktreePath = Path.Combine(temporaryRoot, "Рабочая копия");
        await git.RunAsync(temporaryRoot, "clone", "--bare", "--shared", "--", Path.GetFullPath(args[0]), barePath);
        var branches = await git.GetBranchesAsync(barePath, await git.GetWorktreesAsync(barePath));
        var source = branches.First(branch => !branch.IsRemote);
        var changeService = new GitChangeService(git);
        var comparison = await changeService.CompareBranchesAsync(barePath, source.FullName, source.FullName);
        Check(comparison.BaseHash == comparison.TargetHash && comparison.Ahead == 0 && comparison.Behind == 0
            && comparison.Files.Count == 0 && comparison.Commits.Count == 0, "Сравнение ветки с самой собой");
        var headComparison = await changeService.CompareBranchesAsync(barePath, "HEAD", "HEAD");
        Check(headComparison.BaseHash == headComparison.TargetHash, "Сравнение через HEAD");
        var history = (await git.RunAsync(barePath, "rev-list", "--max-count=2", "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (history.Length == 2)
        {
            await git.RunAsync(barePath, "branch", "comparison-check", history[1]);
            var branchDiff = await changeService.CompareBranchesAsync(barePath, "refs/heads/comparison-check", "HEAD");
            Check(branchDiff.Ahead > 0 && branchDiff.Behind == 0 && branchDiff.Commits.Count > 0
                && branchDiff.MergeBaseHash == history[1], "Сравнение истории и общей точки ответвления без создания коммитов");
            var contribution = await changeService.CompareBranchesAsync(barePath, "HEAD", "refs/heads/comparison-check");
            var versions = await changeService.CompareBranchesAsync(barePath, "HEAD", "refs/heads/comparison-check", BranchComparisonMode.TwoVersions);
            Check(contribution.Files.Count == 0 && contribution.DiffBaseHash == contribution.TargetHash
                && versions.DiffBaseHash == versions.BaseHash && versions.Files.Count > 0,
                "Сравнение двух версий отличается от изменений ветки для отстающей цели");

            var versionFile = versions.Files[0];
            var versionDiff = await changeService.GetBranchRevisionAsync(barePath, versions, versionFile);
            Check(versionDiff.UnifiedText.Length > 0 || versionDiff.Message is not null, "Diff двух версий использует выбранную базовую версию");

            if (branchDiff.Files.FirstOrDefault() is { } changedFile)
            {
                var revision = await changeService.GetBranchRevisionAsync(barePath, branchDiff, changedFile);
                Check(revision.UnifiedText.Length > 0 || revision.Message is not null, "Просмотр файла из сравнения веток");
            }
        }
        var testBranch = "worktree-desk-check-" + Guid.NewGuid().ToString("N");
        await git.CreateWorktreeAsync(barePath, worktreePath, testBranch, source.FullName);
        var created = await git.GetWorktreesAsync(barePath);
        Check(created.Count == 2 && created.Any(tree => tree.Branch == testBranch), "Создание worktree с новой веткой в отдельном тестовом клоне");
        var createdBranch = "worktree-desk-new-branch-" + Guid.NewGuid().ToString("N");
        await git.CreateBranchAsync(worktreePath, createdBranch, source.FullName);
        var activeBranch = (await git.RunAsync(worktreePath, "branch", "--show-current")).Trim();
        Check(activeBranch == createdBranch && (await git.GetWorktreesAsync(barePath)).Any(tree => tree.Branch == createdBranch), "Создание и активация ветки в текущем worktree");

        failedSafely = false;
        try
        {
            await git.CreateBranchAsync(worktreePath, createdBranch, source.FullName);
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && (await git.RunAsync(worktreePath, "branch", "--show-current")).Trim() == createdBranch,
            "Повторное имя ветки не перезаписывает существующую ветку");
        failedSafely = false;

        try
        {
            await git.CreateBranchAsync(worktreePath, "bad?branch", source.FullName);
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely, "Некорректное имя новой ветки отклоняется до переключения");
        failedSafely = false;

        try
        {
            await git.CreateBranchAsync(worktreePath, "safe-name", "--upload-pack=unsafe");
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely, "Исходная ссылка для ветки проверяется по разрешённым refs");
        await git.RunAsync(worktreePath, "switch", "--detach", source.FullName);
        var detachedBranch = "worktree-desk-detached-" + Guid.NewGuid().ToString("N");
        await git.CreateBranchAsync(worktreePath, detachedBranch, "HEAD");
        Check((await git.RunAsync(worktreePath, "branch", "--show-current")).Trim() == detachedBranch,
            "Создание ветки из detached HEAD");
        var previewPath = Path.Combine(worktreePath, "preview.txt");
        File.WriteAllText(previewPath, "Первая строка.\n");
        var untracked = new ChangedFile("??", "preview.txt", null);
        var untrackedDiff = await changeService.GetRevisionAsync(worktreePath, untracked, ChangeScope.WorkingTree);
        Check(untrackedDiff.Lines.Count == 1 && untrackedDiff.Lines[0].Kind == DiffLineKind.Added
            && untrackedDiff.Lines[0].NewText == "Первая строка.", "Просмотр нового текстового файла");
        var newFolder = Path.Combine(worktreePath, "new-folder");
        Directory.CreateDirectory(newFolder);
        await File.WriteAllTextAsync(Path.Combine(newFolder, "nested.txt"), "Новый вложенный файл.\n");
        var nested = (await git.GetStatusAsync(worktreePath, true)).Single(file => file.Path == "new-folder/nested.txt");
        var nestedDiff = await changeService.GetRevisionAsync(worktreePath, nested, ChangeScope.WorkingTree);
        Check(nestedDiff.Lines.Any(line => line.NewText == "Новый вложенный файл."), "Просмотр новых файлов внутри неотслеживаемой папки");
        File.Delete(Path.Combine(newFolder, "nested.txt"));
        Directory.Delete(newFolder);
        await git.RunAsync(worktreePath, "add", "--", "preview.txt");
        File.WriteAllText(previewPath, "Изменённая строка.\nВторая строка.\n");
        var stagedDiff = await changeService.GetRevisionAsync(worktreePath, untracked, ChangeScope.Staged);
        var workingDiff = await changeService.GetRevisionAsync(worktreePath, untracked, ChangeScope.WorkingTree);
        Check(stagedDiff.Lines.Any(line => line.NewText == "Первая строка.")
            && stagedDiff.Lines.All(line => line.NewText != "Вторая строка."), "Просмотр индекса отдельно от незаписанных изменений");
        Check(workingDiff.Lines.Any(line => line.Kind == DiffLineKind.Modified && line.OldText == "Первая строка." && line.NewText == "Изменённая строка.")
            && workingDiff.Lines.Any(line => line.Kind == DiffLineKind.Added && line.NewText == "Вторая строка.")
            && workingDiff.Lines.All(line => line.Kind != DiffLineKind.Added || line.NewText != "Первая строка."), "Просмотр изменений после индексации отдельно");
        var stagedOnlyDiff = await changeService.GetRevisionAsync(worktreePath, untracked, ChangeScope.Staged);
        Check(stagedOnlyDiff.Lines[0].Kind == DiffLineKind.Added, "Выравнивание добавленных строк для двухпанельного diff");
        File.Delete(previewPath);
        var deletedDiff = await changeService.GetRevisionAsync(worktreePath, untracked, ChangeScope.WorkingTree);
        Check(deletedDiff.Lines.Count == 1 && deletedDiff.Lines[0].Kind == DiffLineKind.Removed
            && deletedDiff.Lines[0].OldText == "Первая строка.", "Просмотр удаления содержимого из рабочего дерева");
        await git.RunAsync(worktreePath, "rm", "--cached", "--", "preview.txt");
        var binaryPath = Path.Combine(worktreePath, "binary.dat");
        await File.WriteAllBytesAsync(binaryPath, [0, 1, 2, 3]);
        var binaryDiff = await changeService.GetRevisionAsync(worktreePath, new ChangedFile("??", "binary.dat", null), ChangeScope.WorkingTree);
        Check(binaryDiff.IsBinary && binaryDiff.Message is not null, "Явная пометка бинарного файла");
        var legacyPath = Path.Combine(worktreePath, "legacy.txt");
        await File.WriteAllBytesAsync(legacyPath, [0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2]);
        var legacyDiff = await changeService.GetRevisionAsync(worktreePath, new ChangedFile("??", "legacy.txt", null), ChangeScope.WorkingTree);
        Check(legacyDiff.Lines.Count == 0 && legacyDiff.Message?.Contains("UTF-8", StringComparison.Ordinal) == true,
            "Неизвестная кодировка не показывается как испорченный текст");
        File.Delete(legacyPath);
        var replacementPath = Path.Combine(worktreePath, "replacement.txt");
        await File.WriteAllTextAsync(replacementPath, "Символ: \uFFFD\n");
        await git.RunAsync(worktreePath, "add", "--", "replacement.txt");
        var replacementDiff = await changeService.GetRevisionAsync(worktreePath, new ChangedFile("A ", "replacement.txt", null), ChangeScope.Staged);
        Check(replacementDiff.Lines.Any(line => line.NewText == "Символ: \uFFFD"), "Допустимый символ замены в UTF-8 не скрывает diff");
        await git.RunAsync(worktreePath, "rm", "--cached", "--", "replacement.txt");
        File.Delete(replacementPath);
        var emptyPath = Path.Combine(worktreePath, "empty.txt");
        await File.WriteAllBytesAsync(emptyPath, []);
        var emptyDiff = await changeService.GetRevisionAsync(worktreePath, new ChangedFile("??", "empty.txt", null), ChangeScope.WorkingTree);
        Check(emptyDiff.Lines.Count == 0 && emptyDiff.NewText.Length == 0 && emptyDiff.Message == "Пустой неотслеживаемый файл.", "Пустой неотслеживаемый файл");
        var largePath = Path.Combine(worktreePath, "large.txt");
        await File.WriteAllTextAsync(largePath, new string('x', 600_000));
        await git.RunAsync(worktreePath, "add", "--", "large.txt");
        var largeDiff = await changeService.GetRevisionAsync(worktreePath, new ChangedFile("??", "large.txt", null), ChangeScope.Staged);
        Check(largeDiff.Message == "Diff обрезан: достигнут предел предпросмотра." && largeDiff.UnifiedText.Length <= 500_000 + 40,
            "Ограничение большого staged diff с явным сообщением");
        await git.RunAsync(worktreePath, "rm", "--cached", "--", "large.txt");
        File.Delete(largePath);
        var rejectedTraversal = false;
        try
        {
            await changeService.GetRevisionAsync(worktreePath, new ChangedFile("??", "../outside.txt", null), ChangeScope.WorkingTree);
        }
        catch (GitException)
        {
            rejectedTraversal = true;
        }

        Check(rejectedTraversal, "Защита чтения файлов за пределами worktree");
        File.Delete(binaryPath);
        File.Delete(emptyPath);
        var trackedPaths = (await git.RunAsync(worktreePath, "ls-tree", "-r", "--name-only", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var renameSource = trackedPaths.FirstOrDefault(path => Path.GetExtension(path) is ".md" or ".txt" or ".csproj"
            && File.Exists(Path.Combine(worktreePath, path))
            && new FileInfo(Path.Combine(worktreePath, path)).Length is > 100 and < 10_000);
        if (renameSource is not null)
        {
            var renameTarget = renameSource + ".rename-check";
            var sourcePath = Path.Combine(worktreePath, renameSource);
            var targetPath = Path.Combine(worktreePath, renameTarget);
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            await git.RunAsync(worktreePath, "mv", "--", renameSource, renameTarget);
            await File.AppendAllTextAsync(targetPath, "\nПроверка переименования.\n");
            await git.RunAsync(worktreePath, "add", "-A");
            var rename = (await git.GetStatusAsync(worktreePath)).First(file => file.OriginalPath is not null);
            var renameDiff = await changeService.GetRevisionAsync(worktreePath, rename, ChangeScope.Staged);
            Check(rename.OriginalPath == renameSource && rename.Path == renameTarget
                && renameDiff.UnifiedText.Contains("rename from", StringComparison.Ordinal)
                && renameDiff.Lines.Any(line => line.Kind == DiffLineKind.Added), "Просмотр staged-переименования с изменением");
            await File.WriteAllBytesAsync(targetPath, originalBytes);
            await git.RunAsync(worktreePath, "mv", "--", renameTarget, renameSource);
            await git.RunAsync(worktreePath, "add", "-A");
            await git.RunAsync(worktreePath, "update-index", "--chmod=+x", "--", renameSource);
            var metadata = new ChangedFile("M ", renameSource, null);
            var metadataDiff = await changeService.GetRevisionAsync(worktreePath, metadata, ChangeScope.Staged);
            Check(metadataDiff.Lines.Count == 0 && metadataDiff.Message is not null
                && metadataDiff.UnifiedText.Contains("old mode", StringComparison.Ordinal), "Diff изменения метаданных без текста");
            await git.RunAsync(worktreePath, "update-index", "--chmod=-x", "--", renameSource);
        }

        await git.SwitchBranchAsync(worktreePath, source.Name);
        Check((await git.GetWorktreesAsync(barePath)).Any(tree => tree.Branch == source.Name && !tree.IsBare), "Переключение ветки в тестовой рабочей копии");
        var catalog = await RepositoryCatalog.ResolveAsync(
            [new RepositoryInfo("seed", barePath, "test"), new RepositoryInfo("linked", worktreePath, "test")], git);
        Check(catalog.Repositories.Count == 1 && catalog.Errors.Count == 0, "Worktree и основной репозиторий объединяются в один пункт");
        var dirtyFile = Path.Combine(worktreePath, "worktree-desk-unsaved.txt");
        File.WriteAllText(dirtyFile, "Несохранённые изменения.");
        var branchBeforeDirtyAttempt = (await git.RunAsync(worktreePath, "branch", "--show-current")).Trim();
        var dirtyBranch = "worktree-desk-dirty-" + Guid.NewGuid().ToString("N");
        failedSafely = false;

        try
        {
            await git.CreateBranchAsync(worktreePath, dirtyBranch, "HEAD");
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && File.Exists(dirtyFile)
            && (await git.RunAsync(worktreePath, "branch", "--show-current")).Trim() == branchBeforeDirtyAttempt,
            "Создание ветки в изменённой папке не меняет ветку и сохраняет файлы");
        var dirtyBranchExists = (await git.RunAsync(worktreePath, "branch", "--list", dirtyBranch)).Trim();
        Check(dirtyBranchExists.Length == 0, "При отказе из-за изменений новая ветка не создаётся");
        failedSafely = false;

        try
        {
            await git.SwitchBranchAsync(worktreePath, testBranch);
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && File.Exists(dirtyFile), "Переключение не затрагивает несохранённый файл");
        failedSafely = false;

        try
        {
            await git.RemoveWorktreeAsync(barePath, worktreePath);
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && File.Exists(dirtyFile), "Удаление грязного worktree запрещено");
        File.Delete(dirtyFile);
        await git.RemoveWorktreeAsync(barePath, worktreePath);
        var secondBare = Path.Combine(temporaryRoot, "second.git");
        await git.RunAsync(temporaryRoot, "clone", "--bare", "--shared", "--", Path.GetFullPath(args[0]), secondBare);
        var bundleRoot = Path.Combine(temporaryRoot, "комплект");
        var bundleBranch = "bundle-" + Guid.NewGuid().ToString("N");
        var firstPlan = new WorktreePlan(barePath, Path.Combine(bundleRoot, "Online"), bundleBranch, source.FullName);
        var secondPlan = new WorktreePlan(secondBare, Path.Combine(bundleRoot, "Shared"), bundleBranch, source.FullName);
        var service = new ProjectWorktreeService(git);
        failedSafely = false;

        try
        {
            await service.CreateAsync([firstPlan, secondPlan with { SourceRef = "refs/heads/missing-branch" }]);
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && !Directory.Exists(firstPlan.TargetPath), "Ошибка проверки второго репозитория не создаёт первую копию");
        var bundle = await service.CreateAsync([firstPlan, secondPlan]);
        Check(bundle.Failed is null && bundle.Completed.Count == 2, "Создание комплекта из двух независимых репозиториев");
        Check(Directory.Exists(firstPlan.TargetPath) && Directory.Exists(secondPlan.TargetPath), "Комплект сохраняет соседнее расположение репозиториев");
        await git.RemoveWorktreeAsync(barePath, firstPlan.TargetPath);
        await git.RemoveWorktreeAsync(secondBare, secondPlan.TargetPath);
        var hooksPath = Path.Combine(temporaryRoot, "test-hooks");
        Directory.CreateDirectory(hooksPath);
        File.WriteAllText(Path.Combine(hooksPath, "post-checkout"), "#!/bin/sh\nexit 1\n");
        await git.RunAsync(secondBare, "config", "core.hooksPath", hooksPath.Replace('\\', '/'));
        var partialBranch = "partial-" + Guid.NewGuid().ToString("N");
        var partial = await service.CreateAsync([firstPlan with { Branch = partialBranch }, secondPlan with { Branch = partialBranch }]);
        Check(partial.Failed is not null && partial.Completed.Count >= 1 && Directory.Exists(firstPlan.TargetPath), "Сбой шага сохраняет созданные рабочие копии");
        Check(partial.Completed.Any(plan => GitService.PathsEqual(plan.TargetPath, secondPlan.TargetPath)), "Копия, созданная до ошибки checkout-хука, также учитывается");
        var workspaceService = new WorkspaceService(git);
        var workspace = new ProjectWorkspace
        {
            Name = "Комплект для проверки удаления",
            WorktreePaths = new Dictionary<string, string>
            {
                [barePath] = firstPlan.TargetPath,
                [secondBare] = secondPlan.TargetPath
            }
        };

        var unsavedPath = Path.Combine(secondPlan.TargetPath, "несохранённый.txt");
        File.WriteAllText(unsavedPath, "Не удалять.");
        failedSafely = false;

        try
        {
            await workspaceService.RemoveAsync(workspace);
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && Directory.Exists(firstPlan.TargetPath) && File.Exists(unsavedPath), "Изменения во втором репозитории блокируют удаление всего комплекта");
        File.Delete(unsavedPath);
        var removal = await workspaceService.RemoveAsync(workspace);
        Check(removal.Error is null && removal.RemovedPaths.Count == 2, "Все чистые копии комплекта удаляются одной операцией");
        Check(!Directory.Exists(firstPlan.TargetPath) && !Directory.Exists(secondPlan.TargetPath), "Удаление комплекта не оставляет скрытые рабочие копии");
        await git.RunAsync(barePath, "show-ref", "--verify", $"refs/heads/{partialBranch}");
        await git.RunAsync(secondBare, "show-ref", "--verify", $"refs/heads/{partialBranch}");
        Check(true, "Ветки обоих репозиториев сохраняются после удаления комплекта");
        failedSafely = false;

        try
        {
            await workspaceService.ValidateRemovalAsync(new ProjectWorkspace { WorktreePaths = new Dictionary<string, string> { [barePath] = barePath } });
        }
        catch (GitException)
        {
            failedSafely = true;
        }

        Check(failedSafely && Directory.Exists(barePath), "Основной или bare-репозиторий нельзя удалить как комплект");
        Check(!Directory.Exists(worktreePath), "Удаление чистой дополнительной копии");
        await git.RunAsync(barePath, "show-ref", "--verify", $"refs/heads/{testBranch}");
        Check(true, "Ветка сохраняется после удаления worktree");
        await git.CreateWorktreeAsync(barePath, worktreePath, testBranch, null);
        Check((await git.GetWorktreesAsync(barePath)).Any(tree => tree.Branch == testBranch), "Создание worktree из существующей ветки");
        await git.RemoveWorktreeAsync(barePath, worktreePath);
    }
}
finally
{
    var resolvedRoot = Path.GetFullPath(temporaryRoot);
    if (resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolvedRoot).StartsWith("GitWork-checks-", StringComparison.Ordinal))
    {
        foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(resolvedRoot, true);
    }
}

if (args.Length > 0)
{
    await IndexChecks.RunAsync(args[0], Check);
    await HistoryChecks.RunAsync(args[0], Check);
    await WorkspaceBranchChecks.RunAsync(args[0], Check);
    await GraphChecks.RunAsync(args[0], Check);
    CompactGraphChecks.Run(Check);
    await CompactGraphChecks.RunIntegrationAsync(args.Length > 1 ? args[1] : args[0], Check);
    await RemoteChecks.RunAsync(args[0], Check);
}

await CommitOperationChecks.RunAsync(Check);
await CloneChecks.RunAsync(Check);
await SelectedCommitChecks.RunAsync(Check);
await BranchComparisonChecks.RunAsync(Check);
await WorkspaceRepositoryRemovalChecks.RunAsync(Check);
await PreparationChecks.RunAsync(Check);

Console.WriteLine($"Успешно: {checks} проверок.");
