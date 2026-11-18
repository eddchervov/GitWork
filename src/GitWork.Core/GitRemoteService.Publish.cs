namespace GitWork.Core;

public sealed partial class GitRemoteService
{
    public async Task<RemoteOperationResult> PublishAsync(
        string worktree,
        RemoteRepositoryState expectedState,
        string remoteName,
        string remoteBranch)
    {
        var root = Path.GetFullPath(worktree);
        if (!GitService.PathsEqual(root, expectedState.Worktree))
        {
            throw new GitException("Состояние синхронизации относится к другой рабочей копии. Обновите его и повторите публикацию.");
        }

        if (!expectedState.CanPublish)
        {
            throw new GitException(expectedState.PublicationUnavailableReason ?? "Публикация сейчас недоступна.");
        }

        if (expectedState.HasUpstreamConfiguration || expectedState.UpstreamRemote is not null
            || expectedState.UpstreamBranch is not null || expectedState.UpstreamRef is not null)
        {
            throw new GitException("Для текущей ветки уже настроен upstream.");
        }

        if (string.IsNullOrWhiteSpace(remoteName) || string.IsNullOrWhiteSpace(remoteBranch))
        {
            throw new GitException("Выберите удалённый репозиторий и укажите имя новой ветки.");
        }

        var normalizedBranch = (await this._git.RunAsync(root, "check-ref-format", "--branch", remoteBranch))
            .TrimEnd('\r', '\n');
        if (!string.Equals(normalizedBranch, remoteBranch, StringComparison.Ordinal))
        {
            throw new GitException("Укажите точное имя новой удалённой ветки.");
        }

        var remoteRef = $"refs/heads/{normalizedBranch}";
        await this._git.RunAsync(root, "check-ref-format", remoteRef);

        var current = await this.ReadAsync(root);
        if (!current.CanPublish || current.Branch != expectedState.Branch || current.Head != expectedState.Head
            || current.UpstreamRemote is not null || current.UpstreamBranch is not null || current.UpstreamRef is not null
            || !GitRemoteService.RemoteSnapshotsEqual(expectedState.Remotes, current.Remotes))
        {
            throw new GitException("Ветка, HEAD или настройки удалённых репозиториев изменились. Обновите состояние и повторите публикацию.");
        }

        var branch = current.Branch!;
        var sourceHash = current.Head!;
        var expectedRemote = expectedState.Remotes.FirstOrDefault(remote => remote.Name == remoteName);
        var currentRemote = current.Remotes.FirstOrDefault(remote => remote.Name == remoteName);
        if (expectedRemote?.PushUrl is null || currentRemote?.PushUrl is null)
        {
            throw new GitException("У выбранного удалённого репозитория должен быть настроен ровно один адрес Push.");
        }

        var pushUrls = await GitRemoteService.ReadPushUrlsAsync(this._git, root, remoteName);
        if (pushUrls.Length != 1 || pushUrls[0] != expectedRemote.PushUrl || pushUrls[0] != currentRemote.PushUrl)
        {
            throw new GitException("Адрес Push изменился или для удалённого репозитория настроено несколько адресов. Обновите состояние.");
        }

        var pushUrl = pushUrls[0];
        var fetchUrls = currentRemote.FetchUrls ?? Array.Empty<string>();
        if (fetchUrls.Count != 1 || fetchUrls[0] != pushUrl)
        {
            throw new GitException("Для настройки upstream адреса Fetch и Push должны совпадать и быть единственными.");
        }

        var fetchRefspecs = await this.ReadFetchRefspecsAsync(root, remoteName);
        var standardRefspec = $"refs/heads/*:refs/remotes/{remoteName}/*";
        if (fetchRefspecs.Length != 1
            || fetchRefspecs[0] != standardRefspec && fetchRefspecs[0] != "+" + standardRefspec)
        {
            throw new GitException("Публикация поддерживает только стандартное сопоставление Fetch-веток для выбранного remote.");
        }

        var identity = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
        if (!identity.Matches(branch, null, null, null, sourceHash))
        {
            throw new GitException("Текущая ветка или HEAD изменились перед публикацией. Обновите состояние и повторите.");
        }

        if (await GitRemoteService.HasOperationInProgressAsync(this._git, root))
        {
            throw new GitException("В репозитории выполняется merge, rebase или другая операция. Завершите её перед публикацией.");
        }

        var trackingRef = $"refs/remotes/{remoteName}/{remoteBranch}";
        var trackingHead = (await GitRemoteService.ReadOptionalAsync(
            this._git, root, "rev-parse", "--verify", "--end-of-options", trackingRef))?.Trim();
        var beforePush = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
        var beforePushState = await this.ReadAsync(root);
        var beforePushUrls = await GitRemoteService.ReadPushUrlsAsync(this._git, root, remoteName);
        var beforePushRefspecs = await this.ReadFetchRefspecsAsync(root, remoteName);
        if (!beforePush.Matches(branch, null, null, null, sourceHash)
            || !GitRemoteService.RemoteSnapshotsEqual(expectedState.Remotes, beforePushState.Remotes)
            || beforePushState.Head != sourceHash || beforePushState.Branch != branch
            || beforePushState.HasUpstreamConfiguration
            || beforePushUrls.Length != 1 || beforePushUrls[0] != pushUrl
            || !beforePushRefspecs.SequenceEqual(fetchRefspecs, StringComparer.Ordinal))
        {
            throw new GitException("Ветка, HEAD или настройки remote изменились перед публикацией. Обновите состояние и повторите.");
        }

        string remoteMatches;
        try
        {
            remoteMatches = await this._git.RunAsync(root, "ls-remote", "--heads", "--refs", "--", pushUrl, remoteRef);
        }
        catch (GitException exception)
        {
            throw new GitException(exception.Message.Replace(pushUrl, GitRemoteService.SanitizeUrl(pushUrl), StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(remoteMatches))
        {
            throw new GitException("На сервере уже есть ветка с таким именем. Выберите другое имя или настройте upstream существующей ветки.");
        }

        try
        {
            await this._git.RunAsync(root,
                "push", "--porcelain", $"--force-with-lease={remoteRef}:", "--", pushUrl, $"{sourceHash}:{remoteRef}");
        }
        catch (GitException exception)
        {
            throw new GitException(exception.Message.Replace(pushUrl, GitRemoteService.SanitizeUrl(pushUrl), StringComparison.Ordinal));
        }

        try
        {
            return await this.CompletePublishAsync(root, expectedState, branch, remoteName, remoteBranch,
                remoteRef, trackingRef, trackingHead, sourceHash, pushUrl, fetchRefspecs);
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            await this.TryRemovePartialBranchRemoteAsync(root, branch, remoteName);
            var safeMessage = exception.Message.Replace(pushUrl, GitRemoteService.SanitizeUrl(pushUrl), StringComparison.Ordinal);
            var afterHead = await GitRemoteService.ReadHeadForResultAsync(this._git, root);

            return GitRemoteService.PublishedWithoutUpstream(sourceHash, afterHead,
                $"Ветка опубликована, но локально не удалось настроить upstream: {safeMessage}");
        }
    }

    private async Task<RemoteOperationResult> CompletePublishAsync(
        string root,
        RemoteRepositoryState expectedState,
        string branch,
        string remoteName,
        string remoteBranch,
        string remoteRef,
        string trackingRef,
        string? trackingHead,
        string sourceHash,
        string pushUrl,
        IReadOnlyList<string> fetchRefspecs)
    {
        var afterHead = await GitRemoteService.ReadHeadForResultAsync(this._git, root);
        var afterIdentity = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
        var afterState = await this.ReadAsync(root);
        var afterPushUrls = await GitRemoteService.ReadPushUrlsAsync(this._git, root, remoteName);
        var afterFetchRefspecs = await this.ReadFetchRefspecsAsync(root, remoteName);
        if (!afterIdentity.Matches(branch, null, null, null, sourceHash)
            || afterState.Head != sourceHash || afterState.Branch != branch || afterState.HasUpstreamConfiguration
            || afterState.UpstreamRemote is not null || afterState.UpstreamBranch is not null || afterState.UpstreamRef is not null
            || !GitRemoteService.RemoteSnapshotsEqual(expectedState.Remotes, afterState.Remotes)
            || afterPushUrls.Length != 1 || afterPushUrls[0] != pushUrl
            || !afterFetchRefspecs.SequenceEqual(fetchRefspecs, StringComparer.Ordinal))
        {
            return GitRemoteService.PublishedWithoutUpstream(sourceHash, afterHead,
                "Ветка опубликована, но локальное состояние или настройки изменились. Upstream не настроен.");
        }

        await this._git.RunAsync(root, "update-ref", trackingRef, sourceHash, trackingHead ?? "");

        var beforeConfig = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
        var beforeConfigState = await this.ReadAsync(root);
        var beforeConfigRefspecs = await this.ReadFetchRefspecsAsync(root, remoteName);
        if (!beforeConfig.Matches(branch, null, null, null, sourceHash)
            || beforeConfigState.HasUpstreamConfiguration
            || !GitRemoteService.RemoteSnapshotsEqual(expectedState.Remotes, beforeConfigState.Remotes)
            || !beforeConfigRefspecs.SequenceEqual(fetchRefspecs, StringComparer.Ordinal))
        {
            return GitRemoteService.PublishedWithoutUpstream(sourceHash, afterHead,
                "Ветка опубликована, но локальные настройки изменились. Upstream не настроен.");
        }

        var remoteConfigKey = $"branch.{branch}.remote";
        var mergeConfigKey = $"branch.{branch}.merge";
        var remoteConfigured = false;
        try
        {
            await this._git.RunAsync(root, "config", "--local", "--replace-all", remoteConfigKey, remoteName);
            remoteConfigured = true;
            await this._git.RunAsync(root, "config", "--local", "--replace-all", mergeConfigKey, remoteRef);
        }
        catch (GitException)
        {
            if (remoteConfigured)
            {
                var configuredRemote = (await GitRemoteService.ReadOptionalAsync(
                    this._git, root, "config", "--get", "--", remoteConfigKey))?.Trim();
                var configuredMerge = await GitRemoteService.ReadOptionalAsync(
                    this._git, root, "config", "--get", "--", mergeConfigKey);
                if (configuredRemote == remoteName && configuredMerge is null)
                {
                    await GitRemoteService.ReadOptionalAsync(this._git, root, "config", "--local", "--unset-all", "--", remoteConfigKey);
                }
            }

            return GitRemoteService.PublishedWithoutUpstream(sourceHash, afterHead,
                "Ветка опубликована, но не удалось настроить upstream. Повторная публикация не требуется.");
        }

        return new RemoteOperationResult(false, false, sourceHash, null, afterHead, true,
            $"Ветка опубликована: {remoteName}/{remoteBranch}.");
    }

    private async Task<string[]> ReadFetchRefspecsAsync(string root, string remoteName)
    {
        var output = await GitRemoteService.ReadOptionalAsync(
            this._git, root, "config", "--get-all", "--", $"remote.{remoteName}.fetch");

        return output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    private async Task TryRemovePartialBranchRemoteAsync(string root, string branch, string remoteName)
    {
        try
        {
            var remoteConfigKey = $"branch.{branch}.remote";
            var mergeConfigKey = $"branch.{branch}.merge";
            var configuredRemote = (await GitRemoteService.ReadOptionalAsync(
                this._git, root, "config", "--get", "--", remoteConfigKey))?.Trim();
            var configuredMerge = await GitRemoteService.ReadOptionalAsync(this._git, root, "config", "--get", "--", mergeConfigKey);
            if (configuredRemote == remoteName && configuredMerge is null)
            {
                await GitRemoteService.ReadOptionalAsync(this._git, root, "config", "--local", "--unset-all", "--", remoteConfigKey);
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Не удалось удалить частичную настройку upstream: {0}", exception.Message);
        }
    }

    private static bool RemoteSnapshotsEqual(
        IReadOnlyList<GitRemoteInfo> expected,
        IReadOnlyList<GitRemoteInfo> current)
    {
        if (expected.Count != current.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var expectedRemote = expected[index];
            var currentRemote = current[index];
            if (expectedRemote.Name != currentRemote.Name || expectedRemote.Url != currentRemote.Url
                || expectedRemote.PushUrl != currentRemote.PushUrl
                || !(expectedRemote.FetchUrls ?? Array.Empty<string>())
                    .SequenceEqual(currentRemote.FetchUrls ?? Array.Empty<string>(), StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static RemoteOperationResult PublishedWithoutUpstream(string beforeHead, string? afterHead, string message) =>
        new(false, false, beforeHead, null, afterHead, true, message);
}
