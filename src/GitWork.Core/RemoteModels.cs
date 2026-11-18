namespace GitWork.Core;

public sealed record GitRemoteInfo(
    string Name,
    string Url,
    string? PushUrl = null,
    IReadOnlyList<string>? FetchUrls = null)
{
    public string DisplayUrl => GitRemoteInfo.SanitizeUrl(this.Url);

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
    public bool HasUpstreamConfiguration { get; init; }

    public bool CanPublish => this.PublicationUnavailableReason is null;

    public string? PublicationUnavailableReason
    {
        get
        {
            if (this.IsDetached || string.IsNullOrWhiteSpace(this.Branch))
            {
                return "Сначала выберите локальную ветку.";
            }

            if (this.Head is not { Length: 40 or 64 } head || head.Any(character => !Uri.IsHexDigit(character)))
            {
                return "Ветка ещё не содержит коммитов для публикации.";
            }

            if (this.HasUpstreamConfiguration || this.UpstreamRemote is not null
                || this.UpstreamBranch is not null || this.UpstreamRef is not null)
            {
                return "Для текущей ветки уже настроен upstream.";
            }

            if (!this.Remotes.Any(remote => !string.IsNullOrWhiteSpace(remote.PushUrl)))
            {
                return "Добавьте удалённый репозиторий с одним адресом Push.";
            }

            return null;
        }
    }

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
