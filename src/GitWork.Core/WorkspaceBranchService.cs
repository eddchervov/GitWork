namespace GitWork.Core;

public sealed record WorkspaceBranchPlan(string RepositoryPath, string WorktreePath, string BranchName, string SourceRef);

public sealed record WorkspaceBranchResult(IReadOnlyList<WorkspaceBranchPlan> Completed, WorkspaceBranchPlan? Failed, string? Error);

public sealed class WorkspaceBranchService(GitService git)
{
    private readonly GitService _git = git;

    public async Task<WorkspaceBranchResult> CreateAsync(IReadOnlyList<WorkspaceBranchPlan> plans)
    {
        if (plans.Count == 0)
        {
            throw new GitException("Выберите хотя бы один репозиторий.");
        }

        var prepared = new List<PreparedBranch>();

        foreach (var plan in plans)
        {
            var item = await this.PrepareAsync(plan);

            if (prepared.Any(previous => GitService.PathsEqual(previous.Plan.WorktreePath, plan.WorktreePath)
                || GitService.PathsEqual(previous.RepositoryIdentity, item.RepositoryIdentity) && previous.Plan.BranchName == plan.BranchName))
            {
                throw new GitException("Одна ветка одного Git-репозитория не может быть выбрана в нескольких копиях комплекта.");
            }

            prepared.Add(item);
        }

        var completed = new List<WorkspaceBranchPlan>();

        foreach (var item in prepared)
        {
            try
            {
                var current = await this.PrepareAsync(item.Plan);

                if (current.Head != item.Head || current.HeadRef != item.HeadRef || current.SourceHash != item.SourceHash
                    || !GitService.PathsEqual(current.RepositoryIdentity, item.RepositoryIdentity))
                {
                    throw new GitException("Состояние копии изменилось после проверки. Обновите комплект и повторите операцию.");
                }

                await this._git.RunAsync(item.Plan.WorktreePath, "switch", "--no-track", "-c", item.Plan.BranchName, item.SourceHash);
                completed.Add(item.Plan);
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
            {
                var error = exception.Message;

                try
                {
                    var trees = await this._git.GetWorktreesAsync(item.Plan.RepositoryPath);

                    if (trees.Any(tree => GitService.PathsEqual(tree.Path, item.Plan.WorktreePath) && tree.Branch == item.Plan.BranchName
                        && tree.Head == item.SourceHash))
                    {
                        completed.Add(item.Plan);
                    }
                }
                catch (Exception inspectError) when (inspectError is GitException or IOException or UnauthorizedAccessException)
                {
                    error += "\nНе удалось повторно прочитать состояние: " + inspectError.Message;
                }

                return new WorkspaceBranchResult(completed, item.Plan, error);
            }
        }

        return new WorkspaceBranchResult(completed, null, null);
    }

    private async Task<PreparedBranch> PrepareAsync(WorkspaceBranchPlan plan)
    {
        if (!Path.IsPathFullyQualified(plan.RepositoryPath) || !Path.IsPathFullyQualified(plan.WorktreePath))
        {
            throw new GitException("Укажите полные пути репозитория и рабочей копии.");
        }

        var branchName = (await this._git.RunAsync(plan.WorktreePath, "check-ref-format", "--branch", plan.BranchName)).TrimEnd('\r', '\n');

        if (branchName != plan.BranchName || !WorkspaceBranchService.IsAllowedSource(plan.SourceRef))
        {
            throw new GitException("Проверьте имя новой ветки и исходную ветку.");
        }

        var trees = await this._git.GetWorktreesAsync(plan.RepositoryPath);
        var tree = trees.FirstOrDefault(copy => GitService.PathsEqual(copy.Path, plan.WorktreePath));

        if (tree is null || tree.IsBare || tree.IsPrunable || !Directory.Exists(tree.Path))
        {
            throw new GitException($"Рабочая копия недоступна: {plan.WorktreePath}");
        }

        var actualTrees = await this._git.GetWorktreesAsync(plan.WorktreePath);

        if (actualTrees.Count == 0 || !GitService.PathsEqual(actualTrees[0].Path, trees[0].Path))
        {
            throw new GitException("Папка больше не принадлежит выбранному Git-репозиторию.");
        }

        var changes = await this._git.GetStatusAsync(plan.WorktreePath);

        if (changes.Count > 0)
        {
            throw new GitException($"Есть несохранённые изменения: {plan.WorktreePath}");
        }

        var branches = await this._git.GetBranchesAsync(plan.WorktreePath, trees);

        if (branches.Any(branch => !branch.IsRemote && branch.Name == plan.BranchName))
        {
            throw new GitException($"Ветка «{plan.BranchName}» уже существует: {plan.RepositoryPath}");
        }

        var source = (await this._git.RunAsync(plan.WorktreePath, "rev-parse", "--verify", "--end-of-options", $"{plan.SourceRef}^{{commit}}")).Trim();
        var head = (await this._git.RunAsync(plan.WorktreePath, "rev-parse", "--verify", "HEAD")).Trim();
        var headRef = (await this._git.RunAsync(plan.WorktreePath, "rev-parse", "--symbolic-full-name", "HEAD")).Trim();

        return new PreparedBranch(plan, Path.GetFullPath(trees[0].Path), head, headRef, source);
    }

    private static bool IsAllowedSource(string source) => source == "HEAD"
        || source.StartsWith("refs/heads/", StringComparison.Ordinal)
        || source.StartsWith("refs/remotes/", StringComparison.Ordinal);

    private sealed record PreparedBranch(WorkspaceBranchPlan Plan, string RepositoryIdentity, string Head, string HeadRef, string SourceHash);
}
