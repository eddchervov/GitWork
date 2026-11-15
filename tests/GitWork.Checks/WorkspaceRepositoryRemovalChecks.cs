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
            File.WriteAllText(Path.Combine(firstPath, "README.md"), "Локальное изменение отслеживаемого файла.");
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
            check(dirtyPreview.CanDeleteWorkingCopy && dirtyPreview.ChangedFileCount > 0, "Незакоммиченные изменения требуют подтверждения, но не блокируют удаление worktree");
            var defaultRejected = false;

            try
            {
                await service.RemoveRepositoryAsync(workspace, dirtyPreview, true, [workspace]);
            }
            catch (GitException)
            {
                defaultRejected = true;
            }

            check(defaultRejected && File.Exists(dirtyFile) && Directory.Exists(firstPath)
                && (await git.GetWorktreesAsync(firstRepository)).Any(tree => GitService.PathsEqual(tree.Path, firstPath)),
                "По умолчанию грязный worktree, его папка и регистрация Git сохраняются");

            var untrackedFile = Path.Combine(firstPath, "untracked-local.txt");
            File.WriteAllText(untrackedFile, "Локальный файл.");
            dirtyPreview = await service.PreviewRepositoryRemovalAsync(workspace, firstRepository, [workspace]);
            var removedLast = await service.RemoveRepositoryAsync(workspace, dirtyPreview, true, [workspace], allowUncommittedChanges: true);
            check(!removedLast && !Directory.Exists(firstPath)
                && !(await git.GetWorktreesAsync(firstRepository)).Any(tree => GitService.PathsEqual(tree.Path, firstPath)),
                "Подтверждённое удаление удаляет папку с tracked и untracked изменениями и удаляет регистрацию Git");

            var replacementBranch = "remove-check-replacement-" + Guid.NewGuid().ToString("N");
            await git.CreateWorktreeAsync(firstRepository, firstPath, replacementBranch, sourceRef);

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
            check(ignoredPreview.CanDeleteWorkingCopy && ignoredPreview.IgnoredFileCount > 0, "Игнорируемые файлы не блокируют удаление worktree");
            var wholeRemovalRejected = false;

            try
            {
                await service.ValidateRemovalAsync(cleanWorkspace, [cleanWorkspace]);
            }
            catch (GitException)
            {
                wholeRemovalRejected = true;
            }

            check(!wholeRemovalRejected, "Удаление всего комплекта разрешено при наличии ignored-файлов");

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

            var sourceWorkspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPaths = [sourceRepository],
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourceRepository] = sourceRepository
                }
            };
            var sourcePreview = await service.PreviewRepositoryRemovalAsync(sourceWorkspace, sourceRepository, [sourceWorkspace]);
            var ordinaryMainRejected = false;

            try
            {
                await git.RemoveWorktreeAsync(sourceRepository, sourceRepository, allowUncommittedChanges: true);
            }
            catch (GitException)
            {
                ordinaryMainRejected = true;
            }

            check(!sourcePreview.CanDeleteWorkingCopy && ordinaryMainRejected && Directory.Exists(sourceRepository)
                && (await git.GetWorktreesAsync(sourceRepository)).Any(tree => GitService.PathsEqual(tree.Path, sourceRepository)),
                "Основной worktree обычного репозитория нельзя удалить даже с подтверждением изменений");

            var ignoredFile = Path.Combine(firstPath, "local-secret.txt");
            File.WriteAllText(ignoredFile, "Локальный секрет.");
            var physicalPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, firstRepository, [cleanWorkspace]);
            removedLast = await service.RemoveRepositoryAsync(cleanWorkspace, physicalPreview, true, [cleanWorkspace]);
            check(!removedLast && !Directory.Exists(firstPath) && !File.Exists(ignoredFile)
                && !(await git.GetWorktreesAsync(firstRepository)).Any(tree => GitService.PathsEqual(tree.Path, firstPath))
                && cleanWorkspace.PrimaryRepositoryPath == secondRepository,
                "Физическое удаление worktree удаляет ignored файлы, регистрацию и переназначает основной репозиторий");

            var wholeDirtyFile = Path.Combine(secondPath, "whole-removal.txt");
            File.WriteAllText(wholeDirtyFile, "Незакоммиченные изменения.");
            var wholeDirtyRejected = false;

            try
            {
                await service.RemoveAsync(cleanWorkspace, [cleanWorkspace]);
            }
            catch (GitException)
            {
                wholeDirtyRejected = true;
            }

            check(wholeDirtyRejected && Directory.Exists(secondPath)
                && (await git.GetWorktreesAsync(secondRepository)).Any(tree => GitService.PathsEqual(tree.Path, secondPath)),
                "Удаление всего комплекта по умолчанию сохраняет грязную рабочую копию и её регистрацию");
            var wholeResult = await service.RemoveAsync(cleanWorkspace, [cleanWorkspace], allowUncommittedChanges: true);
            check(wholeResult.FailedPath is null && wholeResult.RemovedPaths.Any(path => GitService.PathsEqual(path, secondPath))
                && !Directory.Exists(secondPath)
                && !(await git.GetWorktreesAsync(secondRepository)).Any(tree => GitService.PathsEqual(tree.Path, secondPath)),
                "Подтверждённое удаление всего комплекта удаляет грязный worktree и его регистрацию");

            var lastPreview = await service.PreviewRepositoryRemovalAsync(cleanWorkspace, secondRepository, [cleanWorkspace]);
            removedLast = await service.RemoveRepositoryAsync(cleanWorkspace, lastPreview, false, [cleanWorkspace]);
            check(removedLast && cleanWorkspace.RepositoryPaths.Count == 0 && cleanWorkspace.WorktreePaths.Count == 0,
                "Удаление последнего репозитория помечает комплект для удаления из настроек");

            var wholeRoot = Path.Combine(root, "whole-root");
            var wholeRepository = Path.Combine(root, "whole-root-source.git");
            var wholeWorktree = Path.Combine(wholeRoot, "source");
            var sibling = Path.Combine(root, "whole-root-sibling");
            Directory.CreateDirectory(wholeRoot);
            Directory.CreateDirectory(sibling);
            await git.RunAsync(root, "clone", "--bare", "--shared", "--", Path.GetFullPath(sourceRepository), wholeRepository);
            await git.CreateWorktreeAsync(wholeRepository, wholeWorktree, "remove-whole-root-" + Guid.NewGuid().ToString("N"), sourceRef);
            Directory.CreateDirectory(Path.Combine(wholeRoot, ".artifacts"));
            File.WriteAllText(Path.Combine(wholeRoot, ".artifacts", "cache.bin"), "cache");
            var wholeWorkspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryPaths = [wholeRepository],
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [wholeRepository] = wholeWorktree
                }
            };
            var parentRootRejected = false;
            try
            {
                await service.ValidateRemovalAsync(wholeWorkspace, [wholeWorkspace], rootPathToDelete: root);
            }
            catch (GitException)
            {
                parentRootRejected = true;
            }

            check(parentRootRejected && Directory.Exists(wholeWorktree), "Нельзя удалить родительскую папку общей папки комплекта");
            var originalInsideRejected = false;
            var workspaceWithOriginalInside = new ProjectWorkspace
            {
                Id = wholeWorkspace.Id,
                RepositoryPaths = [wholeRepository, Path.Combine(wholeRoot, "original")],
                WorktreePaths = new Dictionary<string, string>(wholeWorkspace.WorktreePaths, StringComparer.OrdinalIgnoreCase)
            };
            try
            {
                await service.ValidateRemovalAsync(workspaceWithOriginalInside, [workspaceWithOriginalInside], rootPathToDelete: wholeRoot);
            }
            catch (GitException)
            {
                originalInsideRejected = true;
            }

            check(originalInsideRejected && Directory.Exists(wholeWorktree), "Общая папка с путем исходного репозитория блокируется до удаления worktree");
            var foreignRootRejected = false;
            var foreignWorkspace = new ProjectWorkspace
            {
                Id = Guid.NewGuid().ToString("N"),
                WorktreePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [wholeRepository] = Path.Combine(wholeRoot, "foreign")
                }
            };
            try
            {
                await service.ValidateRemovalAsync(wholeWorkspace, [wholeWorkspace, foreignWorkspace], rootPathToDelete: wholeRoot);
            }
            catch (GitException)
            {
                foreignRootRejected = true;
            }

            check(foreignRootRejected && Directory.Exists(wholeWorktree), "Общая папка, используемая другим комплектом, защищена до удаления worktree");
            var nestedRepository = Path.Combine(wholeRoot, "nested-repository");
            Directory.CreateDirectory(nestedRepository);
            await git.RunAsync(nestedRepository, "init", "--initial-branch=main");
            var nestedRepositoryRejected = false;
            try
            {
                await service.ValidateRemovalAsync(wholeWorkspace, [wholeWorkspace], rootPathToDelete: wholeRoot);
            }
            catch (GitException)
            {
                nestedRepositoryRejected = true;
            }

            check(nestedRepositoryRejected && Directory.Exists(wholeWorktree), "Вложенный Git-репозиторий блокирует удаление общей папки до удаления worktree");
            Directory.Delete(nestedRepository, true);
            var nestedWorktreeRepository = Path.Combine(wholeWorktree, "nested-repository");
            await git.RunAsync(root, "clone", "--", Path.GetFullPath(sourceRepository), nestedWorktreeRepository);
            nestedRepositoryRejected = false;
            try
            {
                await service.ValidateRemovalAsync(wholeWorkspace, [wholeWorkspace], allowUncommittedChanges: true, rootPathToDelete: wholeRoot);
            }
            catch (GitException)
            {
                nestedRepositoryRejected = true;
            }

            check(nestedRepositoryRejected && Directory.Exists(wholeWorktree), "Вложенный в worktree Git-репозиторий защищён от удаления всего комплекта");
            foreach (var file in Directory.EnumerateFiles(nestedWorktreeRepository, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(nestedWorktreeRepository, true);
            var cacheFile = Path.Combine(wholeRoot, ".artifacts", "cache.bin");
            File.SetAttributes(cacheFile, File.GetAttributes(cacheFile) | FileAttributes.ReadOnly);
            await git.RemoveWorktreeAsync(wholeRepository, wholeWorktree);
            var missingDefaultRejected = false;
            try
            {
                await service.RemoveAsync(wholeWorkspace, [wholeWorkspace]);
            }
            catch (GitException)
            {
                missingDefaultRejected = true;
            }

            check(missingDefaultRejected && Directory.Exists(wholeRoot), "Без полного удаления комплект с потерянной рабочей копией не очищает общую папку");
            var wholeRootResult = await service.RemoveAsync(wholeWorkspace, [wholeWorkspace], rootPathToDelete: wholeRoot);
            check(wholeRootResult.FailedPath is null && wholeRootResult.RemovedPaths.Any(path => GitService.PathsEqual(path, wholeWorktree))
                && !Directory.Exists(wholeRoot)
                && Directory.Exists(sibling) && Directory.Exists(sourceRepository),
                "Повторное полное удаление очищает остатки комплекта и сохраняет соседей и исходный репозиторий");
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
