namespace GitWork.Core;

public sealed record WorkspaceRemovalResult(IReadOnlyList<string> RemovedPaths, string? FailedPath, string? Error);
