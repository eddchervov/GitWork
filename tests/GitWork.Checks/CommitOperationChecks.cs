using GitWork.Core;

internal static class CommitOperationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-commit-checks-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var worktreePath = Path.Combine(root, "worktree-from-commit");
        var git = new GitService();
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateDirectory(repository);
            await git.RunAsync(repository, "init", "--initial-branch=main");
            await git.RunAsync(repository, "config", "user.name", "Git Work Test");
            await git.RunAsync(repository, "config", "user.email", "git-work-test@example.invalid");
            var service = new GitCommitOperationService(git);
            var file = Path.Combine(repository, "shared.txt");
            await File.WriteAllTextAsync(file, "base\n");
            await git.RunAsync(repository, "add", "--", "shared.txt");
            await git.RunAsync(repository, "commit", "-m", "base");
            var baseHash = await CommitOperationChecks.ReadHeadAsync(git, repository);
            var sourceBranch = "source-" + Guid.NewGuid().ToString("N");
            await git.RunAsync(repository, "switch", "-c", sourceBranch);
            await File.WriteAllTextAsync(file, "source change\n");
            await git.RunAsync(repository, "add", "--", "shared.txt");
            await git.RunAsync(repository, "commit", "-m", "source change");
            var sourceHash = await CommitOperationChecks.ReadHeadAsync(git, repository);
            await git.RunAsync(repository, "switch", "main");
            await File.WriteAllTextAsync(file, "target change\n");
            await git.RunAsync(repository, "add", "--", "shared.txt");
            await git.RunAsync(repository, "commit", "-m", "target change");
            var mainHash = await CommitOperationChecks.ReadHeadAsync(git, repository);
            var preview = await service.PreviewAsync(repository, sourceHash);
            check(preview.CommitHash == sourceHash && preview.ParentHashes.SequenceEqual([baseHash])
                && preview.CurrentBranch == "main" && preview.CanCherryPick && !preview.IsMerge,
                "Предпросмотр выбранного коммита и текущей целевой ветки");

            var targetRejected = false;
            try
            {
                await service.CherryPickAsync(repository, sourceHash, expectedBranch: "other-branch");
            }
            catch (GitException)
            {
                targetRejected = true;
            }

            check(targetRejected && await CommitOperationChecks.ReadHeadAsync(git, repository) == mainHash
                && !(await service.ReadCherryPickStateAsync(repository)).IsInProgress,
                "Cherry-pick не запускается, если текущая ветка отличается от выбранной цели");

            var newBranch = "from-commit-" + Guid.NewGuid().ToString("N");
            var branchResult = await service.CreateBranchAsync(repository, baseHash, newBranch, false);
            var branchHead = (await git.RunAsync(repository, "rev-parse", newBranch)).Trim();
            check(branchHead == baseHash && branchResult.TargetBranch == "main"
                && (await git.RunAsync(repository, "branch", "--show-current")).Trim() == "main",
                "Создание ветки от коммита, который не является HEAD, без переключения");

            var dirtyPath = Path.Combine(repository, "unsaved.txt");
            await File.WriteAllTextAsync(dirtyPath, "unsaved\n");
            var dirtyRejected = false;
            try
            {
                await service.CherryPickAsync(repository, sourceHash);
            }
            catch (GitException)
            {
                dirtyRejected = true;
            }

            check(dirtyRejected && File.Exists(dirtyPath)
                && (await CommitOperationChecks.ReadHeadAsync(git, repository)) == mainHash,
                "Cherry-pick отклоняет грязную рабочую копию без потери файлов");
            var dirtyBranch = "dirty-safe-" + Guid.NewGuid().ToString("N");
            await service.CreateBranchAsync(repository, baseHash, dirtyBranch, false);
            check(File.Exists(dirtyPath) && (await git.RunAsync(repository, "rev-parse", dirtyBranch)).Trim() == baseHash
                && (await git.RunAsync(repository, "branch", "--show-current")).Trim() == "main",
                "Создание ветки от коммита не затрагивает несохранённые файлы");
            File.Delete(dirtyPath);

            var conflict = await service.CherryPickAsync(repository, sourceHash);
            check(conflict.IsInProgress && conflict.HasConflicts && conflict.TargetBranch == "main",
                "Конфликт cherry-pick возвращается как активная операция");
            await File.WriteAllTextAsync(file, "resolved\n");
            await git.RunAsync(repository, "add", "--", "shared.txt");
            var continueResult = await service.ContinueCherryPickAsync(repository);
            check(!continueResult.IsInProgress && (await service.ReadCherryPickStateAsync(repository)).Message.Contains("не выполняется", StringComparison.Ordinal)
                && (await git.RunAsync(repository, "show", "HEAD:shared.txt")).Trim() == "resolved",
                "Продолжение cherry-pick после разрешения конфликта");

            await git.RunAsync(repository, "switch", "--detach", baseHash);
            var abortSource = "abort-source-" + Guid.NewGuid().ToString("N");
            await git.RunAsync(repository, "switch", "-c", abortSource);
            await File.WriteAllTextAsync(file, "abort incoming\n");
            await git.RunAsync(repository, "add", "--", "shared.txt");
            await git.RunAsync(repository, "commit", "-m", "abort incoming");
            var abortHash = await CommitOperationChecks.ReadHeadAsync(git, repository);
            await git.RunAsync(repository, "switch", "main");
            var headBeforeAbort = await CommitOperationChecks.ReadHeadAsync(git, repository);
            var contentsBeforeAbort = await File.ReadAllTextAsync(file);
            var abortConflict = await service.CherryPickAsync(repository, abortHash);
            check(abortConflict.IsInProgress && abortConflict.HasConflicts, "Второй cherry-pick останавливается на конфликте");
            var aborted = await service.AbortCherryPickAsync(repository);
            check(!aborted.IsInProgress && (await CommitOperationChecks.ReadHeadAsync(git, repository)) == headBeforeAbort
                && await File.ReadAllTextAsync(file) == contentsBeforeAbort,
                "Отмена cherry-pick возвращает исходный коммит и рабочие файлы");

            await git.RunAsync(repository, "switch", sourceBranch);
            var emptyPick = await service.CherryPickAsync(repository, sourceHash);
            check(emptyPick.IsInProgress && emptyPick.IsEmpty && !emptyPick.HasConflicts,
                "Пустой cherry-pick явно помечается и остаётся доступен для отмены");
            await service.AbortCherryPickAsync(repository);
            await git.RunAsync(repository, "switch", "main");

            var invalidHashRejected = false;
            try
            {
                await service.PreviewAsync(repository, "HEAD;--help");
            }
            catch (GitException)
            {
                invalidHashRejected = true;
            }

            check(invalidHashRejected, "Неполный SHA и аргументы вместо ссылки отклоняются");
            var invalidBranchRejected = false;
            try
            {
                await service.CreateBranchAsync(repository, sourceHash, "bad?branch", false);
            }
            catch (GitException)
            {
                invalidBranchRejected = true;
            }

            check(invalidBranchRejected, "Некорректное имя ветки отклоняется до изменений");
            await git.RunAsync(repository, "switch", "main");
            var worktreeBranch = "worktree-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(dirtyPath, "keep this file\n");
            var worktreeResult = await service.CreateWorktreeFromCommitAsync(repository, sourceHash, worktreeBranch, worktreePath);
            var worktrees = await git.GetWorktreesAsync(repository);
            var createdWorktree = worktrees.FirstOrDefault(tree => tree.Branch == worktreeBranch);
            check(worktreeResult.TargetBranch == worktreeBranch && createdWorktree is not null
                && GitService.PathsEqual(createdWorktree.Path, Path.GetFullPath(worktreePath))
                && createdWorktree.Branch == worktreeBranch
                && (await CommitOperationChecks.ReadHeadAsync(git, worktreePath)) == sourceHash
                && File.Exists(dirtyPath),
                "Создание worktree и новой ветки от выбранного SHA");
        }
        finally
        {
            if (Directory.Exists(worktreePath))
            {
                try
                {
                    await git.RunAsync(repository, "worktree", "remove", "--force", "--", worktreePath);
                }
                catch (GitException exception)
                {
                    Console.Error.WriteLine(exception.Message);
                }
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                }

                Directory.Delete(root, true);
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

    private static async Task<string> ReadHeadAsync(GitService git, string repository) =>
        (await git.RunAsync(repository, "rev-parse", "--verify", "HEAD")).TrimEnd('\r', '\n');
}
