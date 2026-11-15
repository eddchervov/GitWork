using GitWork.Core;

namespace GitWork.App;

public sealed record CherryPickBranchChoice(BranchInfo Branch, string CurrentPath)
{
    public bool IsAvailable => this.Branch.OccupiedPath is null || GitService.PathsEqual(this.Branch.OccupiedPath, this.CurrentPath);

    public string DisplayName => this.IsAvailable ? this.Branch.Name : L10n.T("{0} · открыта в другом worktree", this.Branch.Name);

    public override string ToString() => this.DisplayName;
}
