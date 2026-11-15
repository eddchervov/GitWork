namespace GitWork.Core;

public sealed class ProjectWorktreeService(GitService git)
{
    private readonly GitService _git = git;

    public async Task<ProjectCreationResult> CreateAsync(IReadOnlyList<WorktreePlan> plans, IProgress<string>? progress = null)
    {
        if (plans.Count == 0)
        {
            throw new GitException("В проекте нет репозиториев.");
        }

        var targetPaths = plans.Select(plan => System.IO.Path.GetFullPath(plan.TargetPath)).ToArray();

        if (targetPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plans.Count)
        {
            throw new GitException("Рабочие копии должны находиться в разных папках.");
        }

        foreach (var plan in plans)
        {
            progress?.Report($"Проверка {System.IO.Path.GetFileName(plan.RepositoryPath)}…");
            await this._git.ValidateWorktreeCreationAsync(plan.RepositoryPath, plan.TargetPath, plan.Branch, plan.SourceRef);
        }

        var completed = new List<WorktreePlan>();

        foreach (var plan in plans)
        {
            try
            {
                progress?.Report($"Создание {System.IO.Path.GetFileName(plan.RepositoryPath)} · {completed.Count + 1} из {plans.Count}\n{plan.TargetPath}");
                await this._git.CreateWorktreeAsync(plan.RepositoryPath, plan.TargetPath, plan.Branch, plan.SourceRef);
                completed.Add(plan);
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
            {
                var error = exception.Message;

                try
                {
                    var actual = await this._git.GetWorktreesAsync(plan.RepositoryPath);

                    if (actual.Any(tree => GitService.PathsEqual(tree.Path, plan.TargetPath)))
                    {
                        completed.Add(plan);
                    }
                }
                catch (Exception verificationError) when (verificationError is GitException or IOException or UnauthorizedAccessException)
                {
                    error += $"\nНе удалось проверить результат: {verificationError.Message}";
                }

                return new ProjectCreationResult(completed, plan, error);
            }
        }

        return new ProjectCreationResult(completed, null, null);
    }
}
