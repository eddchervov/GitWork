namespace GitWork.Core;

public sealed record BranchDeletionPreview(string Name, string FullRef, string Hash, bool IsMerged);
