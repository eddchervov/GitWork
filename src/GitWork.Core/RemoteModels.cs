namespace GitWork.Core;

public sealed record GitRemoteInfo(
    string Name,
    string Url,
    string? PushUrl = null,
    IReadOnlyList<string>? FetchUrls = null)
{
    public string? DisplayPushUrl => this.PushUrl is null ? null : GitRemoteInfo.SanitizeUrl(this.PushUrl);

    public override string ToString() => this.Name;

    private static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.UserInfo))
        {
            return url;
        }

        var builder = new UriBuilder(parsed) { UserName = "", Password = "" };
        return builder.Uri.ToString();
    }
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
    bool IsDetached)
{
    public bool CanPush => this.PushUnavailableReason is null;

    public string? PushUnavailableReason
    {
        get
        {
            if (this.IsDetached || this.Branch is null)
            {
                return "Push доступен только для ветки, не для detached HEAD.";
            }

            if (this.UpstreamRemote is null || this.UpstreamBranch is null || this.UpstreamRef is null)
            {
                return "У текущей ветки не настроена upstream-ветка.";
            }

            if (this.Remotes.FirstOrDefault(remote => remote.Name == this.UpstreamRemote)?.PushUrl is null)
            {
                return "Для upstream не настроен единственный адрес Push.";
            }

            if (this.Behind > 0)
            {
                return "Удалённая ветка содержит новые коммиты. Сначала выполните Pull или объедините изменения вручную.";
            }

            if (this.Ahead == 0)
            {
                return "Нет локальных коммитов для отправки.";
            }

            if (this.Head is null)
            {
                return "Ветка не содержит коммитов для отправки.";
            }

            return null;
        }
    }
}

public sealed record RemoteOperationResult(
    bool FetchCompleted,
    bool PullCompleted,
    string? BeforeHead,
    string? FetchedHead,
    string? AfterHead,
    bool Success,
    string Message);
