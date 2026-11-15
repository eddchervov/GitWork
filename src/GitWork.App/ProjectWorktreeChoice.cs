using GitWork.Core;

namespace GitWork.App;

public sealed class ProjectWorktreeChoice(RepositoryInfo repository, IReadOnlyList<BranchInfo> branches, BranchInfo? selectedSource)
{
    public RepositoryInfo Repository { get; } = repository;

    public IReadOnlyList<BranchInfo> Branches { get; } = branches;

    public BranchInfo? SelectedSource { get; set; } = selectedSource;
}
