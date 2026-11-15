using GitWork.Core;

public static class WorkspaceBranchChecks
{
    public static async Task RunAsync(string source, Action<bool, string> check)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "GitWork-workspace-branch-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var git = new GitService();
            var service = new WorkspaceBranchService(git);
            var firstClean = Path.Combine(temporaryRoot, "clean-first");
            var secondClean = Path.Combine(temporaryRoot, "clean-second");
            await WorkspaceBranchChecks.CloneAsync(git, temporaryRoot, source, firstClean);
            await WorkspaceBranchChecks.CloneAsync(git, temporaryRoot, source, secondClean);
            var sharedBranchName = "bundle-clean-check";
            var cleanResult = await service.CreateAsync(
            [
                new WorkspaceBranchPlan(firstClean, firstClean, sharedBranchName, "HEAD"),
                new WorkspaceBranchPlan(secondClean, secondClean, sharedBranchName, "HEAD")
            ]);
            check(cleanResult.Failed is null && cleanResult.Completed.Count == 2,
                "Один комплект создаёт ветку в двух чистых независимых репозиториях");
            var firstCleanBranch = (await git.RunAsync(firstClean, "branch", "--show-current")).Trim();
            var secondCleanBranch = (await git.RunAsync(secondClean, "branch", "--show-current")).Trim();
            check(firstCleanBranch == sharedBranchName && secondCleanBranch == sharedBranchName,
                "После операции обе копии комплекта перешли на выбранную ветку");

            var firstPreflight = Path.Combine(temporaryRoot, "preflight-first");
            var secondPreflight = Path.Combine(temporaryRoot, "preflight-second");
            await WorkspaceBranchChecks.CloneAsync(git, temporaryRoot, source, firstPreflight);
            await WorkspaceBranchChecks.CloneAsync(git, temporaryRoot, source, secondPreflight);
            var originalFirstHead = (await git.RunAsync(firstPreflight, "rev-parse", "--verify", "HEAD")).Trim();
            var trackedPath = (await git.RunAsync(secondPreflight, "ls-files", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .First();
            var dirtyPath = Path.Combine(secondPreflight, trackedPath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(dirtyPath, Environment.NewLine + "Workspace branch preflight check");
            var dirtyBranchName = "bundle-dirty-preflight-check";
            var wasBlocked = false;

            try
            {
                await service.CreateAsync(
                [
                    new WorkspaceBranchPlan(firstPreflight, firstPreflight, dirtyBranchName, "HEAD"),
                    new WorkspaceBranchPlan(secondPreflight, secondPreflight, dirtyBranchName, "HEAD")
                ]);
            }
            catch (GitException)
            {
                wasBlocked = true;
            }

            var currentFirstHead = (await git.RunAsync(firstPreflight, "rev-parse", "--verify", "HEAD")).Trim();
            var firstBranchRefs = await git.RunAsync(firstPreflight, "for-each-ref", "--format=%(refname)", $"refs/heads/{dirtyBranchName}");
            check(wasBlocked && currentFirstHead == originalFirstHead && string.IsNullOrWhiteSpace(firstBranchRefs),
                "Изменения во второй копии блокируют комплект до создания первой ветки");

            var sharedRepository = Path.Combine(temporaryRoot, "same-repository");
            var secondWorktree = Path.Combine(temporaryRoot, "same-repository-worktree");
            await WorkspaceBranchChecks.CloneAsync(git, temporaryRoot, source, sharedRepository);
            var helperBranch = "same-repository-helper-check";
            var currentBranch = (await git.RunAsync(sharedRepository, "branch", "--show-current")).Trim();
            await git.CreateWorktreeAsync(sharedRepository, secondWorktree, helperBranch, $"refs/heads/{currentBranch}");
            var duplicateBranchName = "same-repository-bundle-check";
            var duplicateWasRejected = false;

            try
            {
                await service.CreateAsync(
                [
                    new WorkspaceBranchPlan(sharedRepository, sharedRepository, duplicateBranchName, "HEAD"),
                    new WorkspaceBranchPlan(sharedRepository, secondWorktree, duplicateBranchName, "HEAD")
                ]);
            }
            catch (GitException)
            {
                duplicateWasRejected = true;
            }

            var remainingBranchRefs = await git.RunAsync(sharedRepository, "for-each-ref", "--format=%(refname)", $"refs/heads/{duplicateBranchName}");
            check(duplicateWasRejected && string.IsNullOrWhiteSpace(remainingBranchRefs),
                "Одна ветка не назначается двум worktree одного физического репозитория");
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(temporaryRoot);
            if (resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot).StartsWith("GitWork-workspace-branch-checks-", StringComparison.Ordinal))
            {
                foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(resolvedRoot, true);
            }
        }
    }

    private static async Task CloneAsync(GitService git, string workingDirectory, string source, string clonePath)
    {
        await git.RunAsync(workingDirectory, "clone", "--shared", "--", Path.GetFullPath(source), clonePath);
    }
}
