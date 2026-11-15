using GitWork.Core;

namespace GitWork.App;

public sealed class WorkspaceRepositoryRow
{
    public required string RepositoryPath { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public WorktreeInfo? Worktree { get; init; }

    public IReadOnlyList<BranchInfo> Branches { get; init; } = [];

    public BranchInfo? SelectedBranch { get; set; }

    public int ChangeCount { get; init; }

    public IReadOnlyList<ChangedFile> ChangedFiles { get; init; } = [];

    public int ConflictCount => this.ChangedFiles.Count(file => file.Status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU");

    public string? Error { get; init; }

    public bool IsAvailable => this.Error is null && this.Worktree is not null;

    public bool CanSwitch => this.IsAvailable && this.ChangeCount == 0;

    public string BranchLabel => this.Worktree?.BranchLabel ?? "Копия недоступна";

    public string StatusLabel => this.Error ?? (this.ConflictCount > 0
        ? $"Конфликтов: {this.ConflictCount} · изменённых файлов: {this.ChangeCount}"
        : this.ChangeCount == 0 ? "Нет изменений" : $"Изменённых файлов: {this.ChangeCount}");
}
