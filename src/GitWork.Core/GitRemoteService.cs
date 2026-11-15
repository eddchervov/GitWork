namespace GitWork.Core;

public sealed class GitRemoteService(GitService git)
{
    private readonly GitService _git = git;

    public async Task<RemoteRepositoryState> ReadAsync(string worktree)
    {
        var root = System.IO.Path.GetFullPath(worktree);
        var head = await GitRemoteService.ReadHeadAsync(this._git, root);
        var symbolicHead = await GitRemoteService.ReadOptionalAsync(this._git, root, "symbolic-ref", "--quiet", "HEAD");
        var branch = symbolicHead?.Trim().StartsWith("refs/heads/", StringComparison.Ordinal) == true
            ? symbolicHead.Trim()[11..]
            : null;
        var remotes = await GitRemoteService.ReadRemotesAsync(this._git, root);
        var upstreamRemote = branch is null ? null
            : await GitRemoteService.ReadOptionalAsync(this._git, root, "config", "--get", "--", $"branch.{branch}.remote");
        var configuredMerge = branch is null ? null
            : await GitRemoteService.ReadOptionalAsync(this._git, root, "config", "--get", "--", $"branch.{branch}.merge");
        upstreamRemote = upstreamRemote?.Trim();
        configuredMerge = configuredMerge?.Trim();
        string? upstreamRef = null;
        string? upstreamBranch = null;
        var ahead = 0;
        var behind = 0;
        var reason = GitRemoteService.GetPullBlockReason(branch, upstreamRemote, configuredMerge, remotes);
        if (reason is null)
        {
            upstreamRef = await GitRemoteService.ReadOptionalAsync(this._git, root, "rev-parse", "--symbolic-full-name", "--verify", "@{upstream}");
            upstreamRef = upstreamRef?.Trim();
            if (upstreamRef is null || !upstreamRef.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                upstreamRef = null;
                reason = "Git не определил удалённую ветку для слияния.";
            }
            else
            {
                var prefix = $"refs/remotes/{upstreamRemote}/";
                if (!upstreamRef.StartsWith(prefix, StringComparison.Ordinal) || upstreamRef.Length == prefix.Length)
                {
                    upstreamRef = null;
                    reason = "Настройка ветки указывает на другую удалённую ветку.";
                }
                else
                {
                    upstreamBranch = configuredMerge![11..];
                    var counts = (await this._git.RunAsync(root, "rev-list", "--left-right", "--count", $"HEAD...{upstreamRef}"))
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (counts.Length != 2 || !int.TryParse(counts[0], out ahead) || !int.TryParse(counts[1], out behind))
                    {
                        throw new GitException("Не удалось определить состояние ветки относительно удалённой.");
                    }
                }
            }
        }

        if (reason is null)
        {
            if (ahead > 0 && behind > 0)
            {
                reason = "Локальная и удалённая ветки разошлись. Pull с перемоткой вперёд невозможен.";
            }
            else
            {
                reason = await GitRemoteService.ReadOperationBlockReasonAsync(this._git, root);
            }
        }

        return new RemoteRepositoryState(root, branch, head, upstreamRemote, upstreamBranch, upstreamRef,
            ahead, behind, remotes, reason is null, reason, branch is null);
    }

    public async Task<RemoteOperationResult> FetchAsync(string worktree, string remoteName)
    {
        var root = System.IO.Path.GetFullPath(worktree);
        var remotes = await GitRemoteService.ReadRemotesAsync(this._git, root);
        if (!remotes.Any(remote => string.Equals(remote.Name, remoteName, StringComparison.Ordinal)))
        {
            throw new GitException("Выберите удалённый репозиторий из списка.");
        }

        var head = await GitRemoteService.ReadHeadAsync(this._git, root);
        await this._git.RunAsync(root, "fetch", "--", remoteName);
        var after = await GitRemoteService.ReadHeadAsync(this._git, root);

        return new RemoteOperationResult(true, false, head, null, after, true, $"Fetch завершён: {remoteName}.");
    }

    public async Task<RemoteOperationResult> PullAsync(string worktree)
    {
        var root = System.IO.Path.GetFullPath(worktree);
        var before = await GitRemoteService.ReadHeadAsync(this._git, root);
        var initial = await this.ReadAsync(root);
        if (initial.PullUnavailableReason is not null)
        {
            throw new GitException(initial.PullUnavailableReason);
        }

        if (initial.Head != before)
        {
            throw new GitException("Текущая ветка изменилась. Обновите состояние и повторите Pull.");
        }

        var branch = initial.Branch!;
        var upstreamRemote = initial.UpstreamRemote!;
        var upstreamBranch = initial.UpstreamBranch!;
        var upstreamRef = initial.UpstreamRef!;
        await this.EnsureCanIntegrateAsync(root);
        await this._git.RunAsync(root, "fetch", "--", upstreamRemote, $"refs/heads/{upstreamBranch}");

        string? fetchedHead = null;
        try
        {
            fetchedHead = (await this._git.RunAsync(root, "rev-parse", "--verify", "--end-of-options", "FETCH_HEAD^{commit}"))
                .TrimEnd('\r', '\n');
            if (fetchedHead.Length is not (40 or 64) || fetchedHead.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new GitException("Полученная удалённая ветка не указывает на проверенный коммит.");
            }

            var current = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
            if (!current.Matches(branch, upstreamRemote, upstreamRef, upstreamBranch, before))
            {
                return new RemoteOperationResult(true, false, before, fetchedHead, current.Head, false,
                    "Данные получены, но текущая ветка или её связь с удалённой изменились. Pull остановлен.");
            }

            await this.EnsureCanIntegrateAsync(root);
            var counts = (await this._git.RunAsync(root, "rev-list", "--left-right", "--count", $"HEAD...{fetchedHead}"))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (counts.Length != 2 || !int.TryParse(counts[0], out var ahead) || !int.TryParse(counts[1], out var behind))
            {
                throw new GitException("Не удалось определить состояние ветки после Fetch.");
            }

            if (behind == 0)
            {
                return new RemoteOperationResult(true, true, before, fetchedHead, before, true,
                    ahead > 0 ? "Fetch завершён. Локальная ветка опережает удалённую; изменений для Pull нет."
                    : "Fetch и Pull завершены. Ветка уже актуальна.");
            }

            if (ahead > 0)
            {
                throw new GitException("Ветки разошлись: удалённая содержит новые коммиты, но локальная ветка тоже изменилась. Создайте merge или rebase вручную.");
            }

            var beforeMerge = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
            if (!beforeMerge.Matches(branch, upstreamRemote, upstreamRef, upstreamBranch, before))
            {
                throw new GitException("Текущая ветка, её upstream или HEAD изменились перед слиянием. Fetch завершён, Pull остановлен.");
            }

            await this._git.RunAsync(root, "merge", "--ff-only", "--no-edit", fetchedHead);
            var afterIdentity = await GitRemoteService.ReadIdentityAsync(this._git, root, branch);
            if (afterIdentity.Branch != branch || afterIdentity.Head != fetchedHead
                || afterIdentity.UpstreamRemote != upstreamRemote || afterIdentity.UpstreamRef != upstreamRef
                || afterIdentity.UpstreamBranch != upstreamBranch)
            {
                return new RemoteOperationResult(true, false, before, fetchedHead, afterIdentity.Head, false,
                    $"Git завершил слияние, но текущее состояние изменилось параллельно. Текущая ветка: {afterIdentity.Branch ?? "detached HEAD"}; HEAD: {afterIdentity.Head}.");
            }

            return new RemoteOperationResult(true, true, before, fetchedHead, afterIdentity.Head, true, "Fetch и Pull завершены: локальная ветка перемещена вперёд.");
        }
        catch (GitException exception)
        {
            var after = await GitRemoteService.ReadHeadForResultAsync(this._git, root);
            var message = after is not null && fetchedHead is not null && after == fetchedHead && after != before
                ? $"HEAD уже перемещён к полученному коммиту, но Git сообщил об ошибке. Проверьте состояние репозитория. {exception.Message}"
                : $"Fetch завершён, Pull остановлен: {exception.Message}";
            return new RemoteOperationResult(true, false, before, fetchedHead, after, false, message);
        }
    }

    private async Task EnsureCanIntegrateAsync(string worktree)
    {
        var reason = await GitRemoteService.ReadOperationBlockReasonAsync(this._git, worktree);
        if (reason is not null)
        {
            throw new GitException(reason);
        }
    }

    private static async Task<IReadOnlyList<GitRemoteInfo>> ReadRemotesAsync(GitService git, string worktree)
    {
        var output = await git.RunAsync(worktree, "remote");
        var remotes = new List<GitRemoteInfo>();
        foreach (var name in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = await GitRemoteService.ReadOptionalAsync(git, worktree, "config", "--get", "--", $"remote.{name}.url") ?? "";
            remotes.Add(new GitRemoteInfo(name, GitRemoteService.SanitizeUrl(url.Trim())));
        }

        return remotes;
    }

    private static string? GetPullBlockReason(
        string? branch,
        string? remote,
        string? merge,
        IReadOnlyList<GitRemoteInfo> remotes)
    {
        if (branch is null)
        {
            return "Pull доступен только для ветки, не для detached HEAD.";
        }

        if (remote is null || merge is null)
        {
            return "У текущей ветки не настроена upstream-ветка.";
        }

        if (remote == ".")
        {
            return "Локальные upstream-ветки пока не поддерживаются для Pull.";
        }

        if (!remotes.Any(item => item.Name == remote))
        {
            return "Upstream ссылается на удалённый репозиторий, которого больше нет.";
        }

        if (!merge.StartsWith("refs/heads/", StringComparison.Ordinal) || merge.Length == 11)
        {
            return "В настройках upstream задана некорректная ветка.";
        }

        return null;
    }

    private static async Task<string?> ReadOperationBlockReasonAsync(GitService git, string worktree)
    {
        if (await GitRemoteService.HasOperationInProgressAsync(git, worktree))
        {
            return "В репозитории уже выполняется merge, rebase или другая операция.";
        }

        var changes = await git.GetStatusAsync(worktree, true);
        return changes.Count > 0 ? "В рабочей копии есть изменения. Сохраните их перед Pull." : null;
    }

    private static async Task<bool> HasOperationInProgressAsync(GitService git, string worktree)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
        {
            var gitPath = await git.RunAsync(worktree, "rev-parse", "--git-path", marker);
            var resolved = Path.GetFullPath(gitPath.TrimEnd('\r', '\n'), worktree);
            if (File.Exists(resolved) || Directory.Exists(resolved))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> ReadOptionalAsync(GitService git, string worktree, params string[] arguments)
    {
        try
        {
            return await git.RunAsync(worktree, arguments);
        }
        catch (GitException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadHeadAsync(GitService git, string worktree)
    {
        try
        {
            return (await git.RunAsync(worktree, "rev-parse", "--verify", "HEAD")).TrimEnd('\r', '\n');
        }
        catch (GitException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadHeadForResultAsync(GitService git, string worktree)
    {
        try
        {
            return await GitRemoteService.ReadHeadAsync(git, worktree);
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<RemoteIdentity> ReadIdentityAsync(GitService git, string worktree, string branch)
    {
        var symbolicHead = (await GitRemoteService.ReadOptionalAsync(git, worktree, "symbolic-ref", "--quiet", "HEAD"))?.Trim();
        var activeBranch = symbolicHead?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? symbolicHead[11..] : null;
        var head = await GitRemoteService.ReadHeadAsync(git, worktree);
        var remote = (await GitRemoteService.ReadOptionalAsync(git, worktree, "config", "--get", "--", $"branch.{branch}.remote"))?.Trim();
        var upstreamRef = (await GitRemoteService.ReadOptionalAsync(git, worktree, "rev-parse", "--symbolic-full-name", "--verify", "@{upstream}"))?.Trim();
        var merge = (await GitRemoteService.ReadOptionalAsync(git, worktree, "config", "--get", "--", $"branch.{branch}.merge"))?.Trim();
        var upstreamBranch = merge?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? merge[11..] : null;

        return new RemoteIdentity(activeBranch, remote, upstreamRef, upstreamBranch, head);
    }

    private sealed record RemoteIdentity(string? Branch, string? UpstreamRemote, string? UpstreamRef, string? UpstreamBranch, string? Head)
    {
        public bool Matches(string branch, string remote, string upstreamRef, string upstreamBranch, string? head) =>
            this.Branch == branch && this.UpstreamRemote == remote && this.UpstreamRef == upstreamRef
                && this.UpstreamBranch == upstreamBranch && this.Head == head;
    }

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
