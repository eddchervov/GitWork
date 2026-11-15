namespace GitWork.Core;

public sealed record WorktreeState(WorktreeInfo Worktree, IReadOnlyList<ChangedFile> Files, string? Error)
{
    public int ChangeCount => this.Files.Count;

    public int ConflictCount => this.Files.Count(file => file.Status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU");

    public bool IsAvailable => this.Error is null && !this.Worktree.IsBare && !this.Worktree.IsPrunable;

    public string Signature => this.Worktree.Head + "\0" + this.Worktree.Branch + "\0" + this.Error + "\0"
        + string.Join('\0', this.Files.Select(file => file.Status + file.Path + "\0" + file.OriginalPath));
}

public sealed record NavigationStatus(int Changes, int Conflicts, int Unavailable, int Unknown)
{
    public string Label => this.Unavailable > 0 ? $"Недоступно: {this.Unavailable}"
        : this.Conflicts > 0 ? $"Конфликты: {this.Conflicts}"
        : this.Changes > 0 ? $"● {this.Changes}"
        : this.Unknown > 0 ? "…" : "";

    public bool HasProblems => this.Unavailable > 0 || this.Conflicts > 0;

    public string Description => $"Изменённые файлы: {this.Changes}\nКонфликты: {this.Conflicts}\nНедоступные копии: {this.Unavailable}"
        + (this.Unknown > 0 ? $"\nЕщё не проверено: {this.Unknown}" : "");

    public static NavigationStatus Combine(IEnumerable<WorktreeState?> states, int missing = 0)
    {
        var changes = 0;
        var conflicts = 0;
        var unavailable = missing;
        var unknown = 0;

        foreach (var state in states)
        {
            if (state is null)
            {
                unknown++;
                continue;
            }

            changes += state.ChangeCount;
            conflicts += state.ConflictCount;
            unavailable += state.IsAvailable ? 0 : 1;
        }

        return new NavigationStatus(changes, conflicts, unavailable, unknown);
    }
}
