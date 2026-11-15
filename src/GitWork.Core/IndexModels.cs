namespace GitWork.Core;

public sealed record IndexPreview(
    string Worktree,
    ChangedFile File,
    ChangeScope Scope,
    FileDiff Diff,
    string IndexSignature,
    string HeadHash,
    IReadOnlyList<string> HunkPatches,
    string? HunkUnavailableReason,
    string? WorkingFileFingerprint = null);

public sealed record CommitPreview(
    string Repository,
    IReadOnlyList<string> StagedFiles,
    string IndexSignature,
    string HeadHash,
    bool HasConflicts);

public sealed record SelectedCommitFile(ChangedFile File, string Status, string WorkingFileFingerprint);

public sealed record SelectedCommitPreview(
    string Repository,
    IReadOnlyList<SelectedCommitFile> Files,
    string IndexSignature,
    string HeadHash,
    bool HasConflicts);
