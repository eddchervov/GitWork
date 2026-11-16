using GitWork.Core;

namespace GitWork.App;

public sealed record BranchPickerChoice(BranchInfo Branch, string GroupName, bool IsSelectable, string Description);
