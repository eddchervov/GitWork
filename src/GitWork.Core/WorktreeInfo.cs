namespace GitWork.Core;

public sealed record WorktreeInfo(
    string Path,
    string Head,
    string Branch,
    bool IsMain,
    bool IsBare,
    bool IsLocked,
    bool IsPrunable,
    string? Reason)
{
    public string Name => System.IO.Path.GetFileName(this.Path.TrimEnd('/', '\\'));

    public string BranchLabel => string.IsNullOrEmpty(this.Branch) ? "HEAD без ветки" : this.Branch;

    public string KindLabel => this.IsBare ? "BARE" : this.IsMain ? "ОСНОВНАЯ" : "WORKTREE";

    public string Availability => this.IsLocked ? "Заблокирована" : this.IsPrunable ? "Папка недоступна" : "";
}
