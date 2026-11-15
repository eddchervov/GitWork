using GitWork.Core;

namespace GitWork.App;

public sealed class ProjectRepositoryChoice(RepositoryInfo repository, bool isIncluded)
{
    public RepositoryInfo Repository { get; } = repository;

    public bool IsIncluded { get; set; } = isIncluded;
}
