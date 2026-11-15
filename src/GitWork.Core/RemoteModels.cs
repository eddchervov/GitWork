namespace GitWork.Core;

public sealed record GitRemoteInfo(string Name, string Url)
{
    public override string ToString() => this.Name;
}

public sealed record RemoteRepositoryState(
    string Worktree,
    string? Branch,
    string? Head,
    string? UpstreamRemote,
    string? UpstreamBranch,
    string? UpstreamRef,
    int Ahead,
    int Behind,
    IReadOnlyList<GitRemoteInfo> Remotes,
    bool CanPull,
    string? PullUnavailableReason,
    bool IsDetached);

public sealed record RemoteOperationResult(
    bool FetchCompleted,
    bool PullCompleted,
    string? BeforeHead,
    string? FetchedHead,
    string? AfterHead,
    bool Success,
    string Message);
