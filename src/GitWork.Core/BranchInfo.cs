namespace GitWork.Core;

public sealed record BranchInfo(string Name, string FullName, bool IsRemote, string? OccupiedPath)
{
    public bool IsAvailable => this.OccupiedPath is null;

    public string DisplayName => this.OccupiedPath is null ? this.Name : $"{this.Name}  ·  занята";

    public override string ToString() => this.DisplayName;
}
