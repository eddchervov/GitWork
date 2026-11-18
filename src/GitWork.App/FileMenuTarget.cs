using GitWork.Core;

namespace GitWork.App;

internal sealed record FileMenuTarget(
    string Worktree,
    ChangedFile File,
    bool IsWorkingCopy,
    Action? ToggleIncluded = null,
    bool IsIncluded = false);
