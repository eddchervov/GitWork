namespace GitWork.Core;

public sealed record WorkspaceRepositoryRemovalPreview(
    string WorkspaceId,
    string? RootPath,
    string RepositoryPath,
    string? WorktreePath,
    bool IsWorkingCopyAvailable,
    int ChangedFileCount,
    int IgnoredFileCount,
    bool CanDeleteWorkingCopy,
    string? DeleteBlockedReason);
