namespace GitWork.Core;

public enum ChangeScope
{
    WorkingTree,
    Staged
}

public enum DiffLineKind
{
    Equal,
    Added,
    Removed,
    Modified
}

public sealed record DiffLine(int? OldNumber, string OldText, int? NewNumber, string NewText, DiffLineKind Kind);

public sealed record FileDiff(
    string OldText,
    string NewText,
    string UnifiedText,
    IReadOnlyList<DiffLine> Lines,
    bool IsBinary,
    string? Message);

public sealed record CommitInfo(string ShortHash, string Subject, string Author, string Date);

public enum BranchComparisonMode
{
    BranchChanges,
    TwoVersions
}

public sealed record BranchComparison(
    string BaseRef,
    string TargetRef,
    string BaseHash,
    string TargetHash,
    string MergeBaseHash,
    int Ahead,
    int Behind,
    IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<CommitInfo> Commits,
    BranchComparisonMode Mode = BranchComparisonMode.BranchChanges)
{
    public string DiffBaseHash => this.Mode == BranchComparisonMode.TwoVersions ? this.BaseHash : this.MergeBaseHash;
}
