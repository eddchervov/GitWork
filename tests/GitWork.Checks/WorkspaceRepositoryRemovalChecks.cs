using GitWork.Core;

namespace GitWork.Checks;

internal static class WorkspaceRepositoryRemovalChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-remove-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();

        try
        {
            var sourceRepository = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRepository);
            await git.RunAsync(sourceRepository, "init", "--initial-branch=main");
            await git.RunAsync(sourceRepository, "config", "user.name", "Git Work checks");
            await git.RunAsync(sourceRepository, "config", "user.email", "checks@example.invalid");
            File.WriteAllText(Path.Combine(sourceRepository, "README.md"), "Temporary test repository.");
            await git.RunAsync(sourceRepository, "add", "--", "README.md");
            await git.RunAsync(sourceRepository, "commit", "-m", "Test fixture");
            var firstRepository = Path.Combine(root, "first.git");
            var secondRepository = Path.Combine(root, "second.git");
            await git.RunAsync(root, "clone", "--bare", "--shared", "--", Path.GetFullPath(sourceRepository), firstRepository);
            await git.RunAsync(root, "clone", "--bare", "--shared", "--", Path.GetFullPath(sourceRepository), secondRepository);
            var firstPath = Path.Combine(root, "worktrees", "first");
            var secondPath = Path.Combine(root, "worktrees", "second");
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            var firstBranch = "remove-check-first-" + Guid.NewGuid().ToString("N");
            var secondBranch = "remove-check-second-" + Guid.NewGuid().ToString("N");
            var sourceRef = (await git.RunAsync(firstRepository, "symbolic-ref", "HEAD")).Trim();
            await git.CreateWorktreeAsync(firstRepository, firstPath, firstBranch, sourceRef);
            await git.CreateWorktreeAsync(secondRepository, secondPath, secondBranch, sourceRef);
            var workspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPaths = [firstRepository, secondRepository],
                PrimaryRepositoryPath = firstRepository,
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [firstRepository] = firstPath,
                    [secondRepository] = secondPath
                }
            };
            var service = new WorkspaceService(git);
            var stalePreview = await service.PreviewRepositoryRemovalAsync(workspace, firstRepository, [workspace]);
            var dirtyFile = Path.Combine(firstPath, "local-change.txt");
            File.WriteAllText(dirtyFile, "Сохранить на диске.");
            var staleRejected = false;

            try
            {
                await service.RemoveRepositoryAsync(workspace, stalePreview, false, [workspace]);
            }
            catch (GitException)
            {
                staleRejected = true;
            }

            check(staleRejected && workspace.WorktreePaths.ContainsKey(firstRepository), "Устаревший просмотр удаления отклонён без изменения состава комплекта");
            var dirtyPreview = await service.PreviewRepositoryRemovalAsync(workspace, firstRepository, [workspace]);
            check(!dirtyPreview.CanDeleteWorkingCopy && dirtyPreview.ChangedFileCount > 0, "Грязную рабочую копию нельзя удалить с диска");
            var removedLast = await service.RemoveRepositoryAsync(workspace, dirtyPreview, false, [workspace]);
            check(!removedLast && File.Exists(dirtyFile) && Directory.Exists(firstPath), "Убрать грязный репозиторий из комплекта можно, сохранив папку");

            var cleanWorkspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPaths = [firstRepository, secondRepository],
                PrimaryRepositoryPath = firstRepository,
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [firstRepository] = firstPath,
                    [secondRepository] = secondPath
                }
            };
            File.Delete(dirtyFile);
            File.WriteAllText(Path.Combine(firstRepository, "info", "exclude"), "local-secret.txt\n");
            File.WriteAllText(Path.Combine(firstPath, "local-secret.txt"), "Локальный секрет.");
            var ignoredPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, firstRepository, [cleanWorkspace]);
            check(!ignoredPreview.CanDeleteWorkingCopy && ignoredPreview.IgnoredFileCount > 0, "Игнорируемый локальный файл защищает рабочую копию от удаления");
            var wholeRemovalRejected = false;

            try
            {
                await service.ValidateRemovalAsync(cleanWorkspace, [cleanWorkspace]);
            }
            catch (GitException)
            {
                wholeRemovalRejected = true;
            }

            check(wholeRemovalRejected && Directory.Exists(firstPath) && Directory.Exists(secondPath),
                "Проверка всего комплекта находит ignored-файлы до удаления первой копии");
            File.Delete(Path.Combine(firstPath, "local-secret.txt"));

            await git.RunAsync(secondRepository, "worktree", "lock", "--", secondPath);
            var lockedPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, secondRepository, [cleanWorkspace]);
            check(!lockedPreview.CanDeleteWorkingCopy, "Заблокированную рабочую копию нельзя удалить с диска");
            await git.RunAsync(secondRepository, "worktree", "unlock", "--", secondPath);

            var otherWorkspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPaths = [secondRepository],
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [secondRepository] = secondPath
                }
            };
            var sharedPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, secondRepository, [cleanWorkspace, otherWorkspace]);
            check(!sharedPreview.CanDeleteWorkingCopy, "Рабочая копия, используемая другим комплектом, не удаляется с диска");
            wholeRemovalRejected = false;

            try
            {
                await service.ValidateRemovalAsync(cleanWorkspace, [cleanWorkspace, otherWorkspace]);
            }
            catch (GitException)
            {
                wholeRemovalRejected = true;
            }

            check(wholeRemovalRejected && Directory.Exists(firstPath) && Directory.Exists(secondPath),
                "Удаление всего комплекта блокируется, если копия используется другим комплектом");

            var mainWorkspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPaths = [firstRepository],
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [firstRepository] = firstRepository
                }
            };
            var mainPreview = await service.PreviewRepositoryRemovalAsync(mainWorkspace, firstRepository, [mainWorkspace]);
            check(!mainPreview.CanDeleteWorkingCopy && mainWorkspace.WorktreePaths.ContainsKey(firstRepository), "Основной репозиторий нельзя удалить с диска");

            var physicalPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, firstRepository, [cleanWorkspace]);
            removedLast = await service.RemoveRepositoryAsync(cleanWorkspace, physicalPreview, true, [cleanWorkspace]);
            check(!removedLast && !Directory.Exists(firstPath) && cleanWorkspace.PrimaryRepositoryPath == secondRepository,
                "Физическое удаление чистой копии убирает обе ссылки и переназначает основной репозиторий");
            var lastPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, secondRepository, [cleanWorkspace]);
            removedLast = await service.RemoveRepositoryAsync(cleanWorkspace, lastPreview, false, [cleanWorkspace]);
            check(removedLast && cleanWorkspace.RepositoryPaths.Count == 0 && cleanWorkspace.WorktreePaths.Count == 0,
                "Удаление последнего репозитория помечает комплект для удаления из настроек");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, true);
            }
        }
    }
}
