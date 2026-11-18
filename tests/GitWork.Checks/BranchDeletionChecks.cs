using System.Text;
using GitWork.Core;

internal static class BranchDeletionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-branch-deletion-checks-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var occupiedWorktree = Path.Combine(root, "occupied-worktree");
        var bareRepository = Path.Combine(root, "bare.git");
        var git = new GitService();
        var service = new GitBranchDeletionService(git);
        Directory.CreateDirectory(repository);

        try
        {
            await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
            await git.RunAsync(repository, "config", "user.name", "Git Work Branch Deletion Checks");
            await git.RunAsync(repository, "config", "user.email", "branch-deletion@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repository, "base.txt"), "base\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--all");
            await git.RunAsync(repository, "commit", "-m", "base");

            await git.RunAsync(repository, "switch", "-c", "merged-branch");
            await File.WriteAllTextAsync(Path.Combine(repository, "merged.txt"), "merged\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--", "merged.txt");
            await git.RunAsync(repository, "commit", "-m", "merged branch change");
            var mergedHash = (await git.RunAsync(repository, "rev-parse", "HEAD")).Trim();
            await git.RunAsync(repository, "switch", "main");
            await git.RunAsync(repository, "merge", "--ff-only", "merged-branch");

            var upstreamBehindBase = (await git.RunAsync(repository, "rev-parse", "HEAD")).Trim();
            await git.RunAsync(repository, "branch", "upstream-behind", upstreamBehindBase);
            await git.RunAsync(repository, "switch", "upstream-behind");
            await File.WriteAllTextAsync(Path.Combine(repository, "upstream-only.txt"), "upstream fixture\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--", "upstream-only.txt");
            await git.RunAsync(repository, "commit", "-m", "upstream branch commit");
            var upstreamBehindHash = (await git.RunAsync(repository, "rev-parse", "HEAD")).Trim();
            await git.RunAsync(repository, "switch", "main");
            await git.RunAsync(repository, "merge", "--ff-only", "upstream-behind");
            await git.RunAsync(repository, "update-ref", "refs/remotes/origin/upstream-behind", upstreamBehindBase);
            await git.RunAsync(repository, "remote", "add", "origin", "https://example.invalid/repository.git");
            await git.RunAsync(repository, "branch", "--set-upstream-to=origin/upstream-behind", "upstream-behind");

            var upstreamPreview = await service.GetPreviewAsync(repository, "upstream-behind");
            var upstreamDeleteBlocked = false;
            try
            {
                await service.DeleteAsync(repository, upstreamPreview);
            }
            catch (GitException)
            {
                upstreamDeleteBlocked = await BranchDeletionChecks.BranchExistsAsync(git, repository, "upstream-behind");
            }

            var nativeDeleteBlocked = false;
            try
            {
                await git.RunAsync(repository, "branch", "-d", "--", "upstream-behind");
            }
            catch (GitException)
            {
                nativeDeleteBlocked = await BranchDeletionChecks.BranchExistsAsync(git, repository, "upstream-behind");
            }

            check(upstreamPreview.Hash == upstreamBehindHash && !upstreamPreview.IsMerged,
                "Preview считает ветку неслитой, если она опережает upstream, даже когда её коммит уже в HEAD");
            check(upstreamDeleteBlocked, "Удаление такой ветки без force отклоняется сервисом");
            check(nativeDeleteBlocked, "Нативный git branch -d отклоняет удаление такой ветки");

            await git.RunAsync(repository, "branch", "stale-branch", "HEAD~2");
            var stalePreview = await service.GetPreviewAsync(repository, "stale-branch");
            await git.RunAsync(repository, "update-ref", stalePreview.FullRef, mergedHash);

            await git.RunAsync(repository, "switch", "-c", "unmerged-branch");
            await File.WriteAllTextAsync(Path.Combine(repository, "unmerged.txt"), "unmerged\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--", "unmerged.txt");
            await git.RunAsync(repository, "commit", "-m", "unmerged branch change");
            await git.RunAsync(repository, "switch", "main");
            var unmergedPreview = await service.GetPreviewAsync(repository, "unmerged-branch");
            var unmergedDeleteBlocked = false;
            try
            {
                await service.DeleteAsync(repository, unmergedPreview);
            }
            catch (GitException)
            {
                unmergedDeleteBlocked = await BranchDeletionChecks.BranchExistsAsync(git, repository, "unmerged-branch");
            }

            check(!unmergedPreview.IsMerged && unmergedDeleteBlocked,
                "Ветка без merge-защиты не удаляется, пока не подтверждён force");
            await service.DeleteAsync(repository, unmergedPreview, force: true);
            check(!await BranchDeletionChecks.BranchExistsAsync(git, repository, "unmerged-branch"),
                "Явно подтверждённое принудительное удаление удаляет unmerged-ветку");

            var dirtyPath = Path.Combine(repository, "local.txt");
            await File.WriteAllTextAsync(dirtyPath, "keep while deleting another branch\n", new UTF8Encoding(false));
            var mergedPreview = await service.GetPreviewAsync(repository, "merged-branch");
            check(mergedPreview.IsMerged && mergedPreview.Hash == mergedHash,
                "Preview содержит full ref, hash и признак merge относительно HEAD");
            await service.DeleteAsync(repository, mergedPreview);
            check(!await BranchDeletionChecks.BranchExistsAsync(git, repository, "merged-branch") && File.Exists(dirtyPath),
                "Merged-ветка удаляется через обычный -d без блокировки из-за грязной рабочей копии");

            var staleDeleteBlocked = false;
            try
            {
                await service.DeleteAsync(repository, stalePreview, force: true);
            }
            catch (GitException)
            {
                staleDeleteBlocked = await BranchDeletionChecks.BranchExistsAsync(git, repository, "stale-branch")
                    && (await git.RunAsync(repository, "rev-parse", "refs/heads/stale-branch")).Trim() == mergedHash;
            }

            check(staleDeleteBlocked, "Устаревший preview блокируется после изменения hash ветки");

            await git.RunAsync(repository, "worktree", "add", "-b", "occupied-branch", "--", occupiedWorktree, "HEAD");
            var occupiedDeleteBlocked = false;
            try
            {
                await service.GetPreviewAsync(repository, "occupied-branch");
            }
            catch (GitException)
            {
                occupiedDeleteBlocked = await BranchDeletionChecks.BranchExistsAsync(git, repository, "occupied-branch");
            }

            check(occupiedDeleteBlocked, "Занятая в другом worktree ветка защищена от удаления");

            var currentDeleteBlocked = false;
            try
            {
                await service.GetPreviewAsync(repository, "main");
            }
            catch (GitException)
            {
                currentDeleteBlocked = (await git.RunAsync(repository, "branch", "--show-current")).Trim() == "main";
            }

            check(currentDeleteBlocked, "Текущая ветка защищена от удаления");

            await git.RunAsync(repository, "update-ref", "refs/remotes/origin/remote-only", "HEAD");
            var remoteDeleteBlocked = false;
            try
            {
                await service.GetPreviewAsync(repository, "origin/remote-only");
            }
            catch (GitException)
            {
                remoteDeleteBlocked = (await git.RunAsync(repository, "show-ref", "--verify", "refs/remotes/origin/remote-only")).Length > 0;
            }

            check(remoteDeleteBlocked, "Удалённая ветка не принимается как цель локального удаления");

            await git.RunAsync(root, "init", "--bare", "--initial-branch=main", "-q", bareRepository);
            var bareDeleteBlocked = false;
            try
            {
                await service.GetPreviewAsync(bareRepository, "main");
            }
            catch (GitException)
            {
                bareDeleteBlocked = true;
            }

            check(bareDeleteBlocked, "Bare-репозиторий защищён от удаления веток");
        }
        finally
        {
            if (Directory.Exists(occupiedWorktree))
            {
                try
                {
                    await git.RunAsync(repository, "worktree", "remove", "--force", "--", occupiedWorktree);
                }
                catch (GitException exception)
                {
                    Console.Error.WriteLine(exception.Message);
                }
            }

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

    private static async Task<bool> BranchExistsAsync(GitService git, string repository, string branchName)
    {
        var branches = await git.RunAsync(repository, "for-each-ref", "--format=%(refname)", "refs/heads");
        return branches.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains($"refs/heads/{branchName}", StringComparer.Ordinal);
    }
}
