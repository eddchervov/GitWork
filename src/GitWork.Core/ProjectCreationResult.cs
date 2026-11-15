namespace GitWork.Core;

public sealed record ProjectCreationResult(IReadOnlyList<WorktreePlan> Completed, WorktreePlan? Failed, string? Error);
