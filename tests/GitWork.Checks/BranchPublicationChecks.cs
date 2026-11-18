using System.Text;
using GitWork.Core;

internal static class BranchPublicationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-branch-publication-checks-" + Guid.NewGuid().ToString("N"));
        var firstBare = Path.Combine(root, "first.git");
        var secondBare = Path.Combine(root, "second.git");
        var client = Path.Combine(root, "client");
        var git = new GitService();
        var service = new GitRemoteService(git);
        Directory.CreateDirectory(root);

        try
        {
            await git.RunAsync(root, "init", "--bare", "--initial-branch=main", "-q", firstBare);
            await git.RunAsync(root, "init", "--bare", "--initial-branch=main", "-q", secondBare);
            await BranchPublicationChecks.InitializeClientAsync(git, client);
            await git.RunAsync(client, "remote", "add", "origin", firstBare);
            await git.RunAsync(client, "switch", "-c", "feature/publish");

            var baseHead = (await git.RunAsync(client, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(client, "tracked.txt"), "staged before publish\n", new UTF8Encoding(false));
            await git.RunAsync(client, "add", "--", "tracked.txt");
            await File.WriteAllTextAsync(Path.Combine(client, "tracked.txt"), "working before publish\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(client, "untracked.txt"), "untracked before publish\n", new UTF8Encoding(false));
            var expectedState = await service.ReadAsync(client);
            var initialIndex = await git.RunAsync(client, "show", ":tracked.txt");
            var initialStatus = await git.GetStatusAsync(client, includeAllUntrackedFiles: true);

            check(expectedState.CanPublish && expectedState.Branch == "feature/publish"
                && expectedState.Head == baseHead && expectedState.UpstreamRemote is null,
                "Новая локальная ветка с коммитом и единственным remote доступна для публикации даже с локальными изменениями");

            var publish = await service.PublishAsync(client, expectedState, "origin", "published/first");
            var publishedHead = (await git.RunAsync(firstBare, "rev-parse", "refs/heads/published/first")).Trim();
            var publishedState = await service.ReadAsync(client);
            check(publish.Success && publishedHead == baseHead
                && publishedState.UpstreamRemote == "origin"
                && publishedState.UpstreamBranch == "published/first"
                && publishedState.UpstreamRef == "refs/remotes/origin/published/first"
                && publishedState.Ahead == 0 && publishedState.Behind == 0,
                "Публикация создаёт выбранную удалённую ветку и настраивает стандартный upstream");
            check((await git.RunAsync(client, "show", ":tracked.txt")) == initialIndex
                && await File.ReadAllTextAsync(Path.Combine(client, "tracked.txt")) == "working before publish\n"
                && await File.ReadAllTextAsync(Path.Combine(client, "untracked.txt")) == "untracked before publish\n"
                && (await git.GetStatusAsync(client, includeAllUntrackedFiles: true)).Count == initialStatus.Count,
                "Публикация не меняет индекс и рабочие файлы");

            await git.RunAsync(client, "restore", "--staged", "--worktree", "--", "tracked.txt");
            File.Delete(Path.Combine(client, "untracked.txt"));
            await File.WriteAllTextAsync(Path.Combine(client, "advance.txt"), "next commit\n", new UTF8Encoding(false));
            await git.RunAsync(client, "add", "--", "advance.txt");
            await git.RunAsync(client, "commit", "-m", "advance after publish");
            var pushState = await service.ReadAsync(client);
            var pushResult = await service.PushAsync(client, pushState);
            var pushedHead = (await git.RunAsync(firstBare, "rev-parse", "refs/heads/published/first")).Trim();
            check(pushResult.Success && pushedHead == (await git.RunAsync(client, "rev-parse", "HEAD")).Trim()
                && (await service.ReadAsync(client)).Ahead == 0,
                "После публикации настроенный upstream поддерживает обычный Push");

            await BranchPublicationChecks.CheckCreateOnlyAsync(root, secondBare, git, service, check);
            await BranchPublicationChecks.CheckPostPushReadFailureAsync(root, git, service, check);
            await BranchPublicationChecks.CheckUnavailableStatesAsync(root, firstBare, git, service, check);
            await BranchPublicationChecks.CheckFailedPublishAsync(root, git, service, check);
            await BranchPublicationChecks.CheckMultiplePushUrlsAsync(root, firstBare, secondBare, git, service, check);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(root, true);
            }
        }
    }

    private static async Task CheckCreateOnlyAsync(
        string root,
        string bareRepository,
        GitService git,
        GitRemoteService service,
        Action<bool, string> check)
    {
        var repository = Path.Combine(root, "existing-client");
        await BranchPublicationChecks.InitializeClientAsync(git, repository);
        await git.RunAsync(repository, "remote", "add", "origin", bareRepository);
        var head = (await git.RunAsync(repository, "rev-parse", "HEAD")).Trim();
        await git.RunAsync(repository, "push", "--", bareRepository, $"{head}:refs/heads/already-there");
        var state = await service.ReadAsync(repository);
        var refsBefore = await git.RunAsync(bareRepository, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads");
        var configBefore = await BranchPublicationChecks.ReadBranchConfigAsync(git, repository);
        var rejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(repository, state, "origin", "already-there"));
        var refsAfter = await git.RunAsync(bareRepository, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads");
        var configAfter = await BranchPublicationChecks.ReadBranchConfigAsync(git, repository);
        check(rejected && refsAfter == refsBefore && configAfter == configBefore,
            "Публикация отклоняет уже существующую удалённую ветку без изменения refs и upstream-конфигурации");
    }

    private static async Task CheckPostPushReadFailureAsync(
        string root,
        GitService git,
        GitRemoteService service,
        Action<bool, string> check)
    {
        var bareRepository = Path.Combine(root, "post-push-hook.git");
        var repository = Path.Combine(root, "post-push-hook-client");
        await git.RunAsync(root, "init", "--bare", "--initial-branch=main", "-q", bareRepository);
        await BranchPublicationChecks.InitializeClientAsync(git, repository);
        await git.RunAsync(repository, "remote", "add", "origin", bareRepository);
        var expectedState = await service.ReadAsync(repository);
        var sourceHash = expectedState.Head!;
        var clientConfig = Path.Combine(repository, ".git", "config");
        var originalConfig = await File.ReadAllBytesAsync(clientConfig);
        var hookPath = Path.Combine(bareRepository, "hooks", "post-receive");
        var shellConfigPath = BranchPublicationChecks.ToGitBashPath(clientConfig).Replace("'", "'\\''", StringComparison.Ordinal);
        await File.WriteAllTextAsync(hookPath, $"#!/bin/sh\necho '[broken' > '{shellConfigPath}'\n", new UTF8Encoding(false));

        try
        {
            var result = await service.PublishAsync(repository, expectedState, "origin", "post-push-read-error");
            var remoteHash = (await git.RunAsync(bareRepository, "rev-parse", "refs/heads/post-push-read-error")).Trim();
            check(result.Success && result.Message.Contains("опубликована", StringComparison.OrdinalIgnoreCase)
                && result.Message.Contains("upstream", StringComparison.OrdinalIgnoreCase) && remoteHash == sourceHash,
                "Ошибка чтения локального состояния после push сообщает об успешной публикации и сохраняет hash на remote");
        }
        finally
        {
            await File.WriteAllBytesAsync(clientConfig, originalConfig);
        }

        check((await BranchPublicationChecks.ReadBranchConfigAsync(git, repository)).Length == 0,
            "После восстановления fixture upstream-конфигурация публикации осталась незаписанной");
    }

    private static async Task CheckUnavailableStatesAsync(
        string root,
        string bareRepository,
        GitService git,
        GitRemoteService service,
        Action<bool, string> check)
    {
        var missingRemote = Path.Combine(root, "missing-remote-client");
        await BranchPublicationChecks.InitializeClientAsync(git, missingRemote);
        var missingState = await service.ReadAsync(missingRemote);
        var missingRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(missingRemote, missingState, "origin", "new-branch"));
        check(!missingState.CanPublish && missingState.PublicationUnavailableReason is not null && missingRejected,
            "Публикация без remote недоступна с объяснением причины");

        var noHead = Path.Combine(root, "unborn-client");
        await BranchPublicationChecks.InitializeClientAsync(git, noHead, createCommit: false);
        await git.RunAsync(noHead, "remote", "add", "origin", bareRepository);
        var unbornState = await service.ReadAsync(noHead);
        var unbornRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(noHead, unbornState, "origin", "unborn-branch"));
        check(!unbornState.CanPublish && unbornState.Head is null && unbornRejected,
            "Публикация из unborn-ветки отклоняется");

        var detached = Path.Combine(root, "detached-client");
        await BranchPublicationChecks.InitializeClientAsync(git, detached);
        await git.RunAsync(detached, "remote", "add", "origin", bareRepository);
        await git.RunAsync(detached, "switch", "--detach", "HEAD");
        var detachedState = await service.ReadAsync(detached);
        var detachedRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(detached, detachedState, "origin", "detached-branch"));
        check(!detachedState.CanPublish && detachedState.IsDetached && detachedRejected,
            "Публикация из detached HEAD отклоняется");

        var staleHead = Path.Combine(root, "stale-head-client");
        await BranchPublicationChecks.InitializeClientAsync(git, staleHead);
        await git.RunAsync(staleHead, "remote", "add", "origin", bareRepository);
        var staleState = await service.ReadAsync(staleHead);
        await File.WriteAllTextAsync(Path.Combine(staleHead, "next.txt"), "next\n", new UTF8Encoding(false));
        await git.RunAsync(staleHead, "add", "--", "next.txt");
        await git.RunAsync(staleHead, "commit", "-m", "advance after snapshot");
        var staleRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(staleHead, staleState, "origin", "stale-branch"));
        var staleConfig = await BranchPublicationChecks.ReadBranchConfigAsync(git, staleHead);
        check(staleRejected && staleConfig.Length == 0
            && await BranchPublicationChecks.ReferenceExistsAsync(git, bareRepository, "refs/heads/stale-branch") == false,
            "Изменённый после snapshot HEAD блокирует публикацию до изменений remote и branch config");

        var staleUrl = Path.Combine(root, "stale-url-client");
        await BranchPublicationChecks.InitializeClientAsync(git, staleUrl);
        await git.RunAsync(staleUrl, "remote", "add", "origin", bareRepository);
        var staleUrlState = await service.ReadAsync(staleUrl);
        await git.RunAsync(staleUrl, "remote", "set-url", "--push", "origin", Path.Combine(root, "different.git"));
        var staleUrlRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(staleUrl, staleUrlState, "origin", "url-branch"));
        check(staleUrlRejected && (await BranchPublicationChecks.ReadBranchConfigAsync(git, staleUrl)).Length == 0
            && await BranchPublicationChecks.ReferenceExistsAsync(git, bareRepository, "refs/heads/url-branch") == false,
            "Изменившийся после snapshot URL блокирует публикацию без настройки upstream");

        var brokenUpstream = Path.Combine(root, "broken-upstream-client");
        await BranchPublicationChecks.InitializeClientAsync(git, brokenUpstream);
        await git.RunAsync(brokenUpstream, "remote", "add", "origin", bareRepository);
        await git.RunAsync(brokenUpstream, "config", "branch.main.remote", "missing-remote");
        var brokenState = await service.ReadAsync(brokenUpstream);
        var brokenRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(brokenUpstream, brokenState, "origin", "broken-config-branch"));
        check(!brokenState.CanPublish && brokenState.UpstreamRemote == "missing-remote" && brokenRejected
            && (await git.RunAsync(brokenUpstream, "config", "--get", "branch.main.remote")).Trim() == "missing-remote",
            "Сломанная upstream-конфигурация не перезаписывается публикацией");

        var mergeOnly = Path.Combine(root, "merge-only-client");
        await BranchPublicationChecks.InitializeClientAsync(git, mergeOnly);
        await git.RunAsync(mergeOnly, "remote", "add", "origin", bareRepository);
        await git.RunAsync(mergeOnly, "config", "branch.main.merge", "refs/heads/manual-target");
        var mergeOnlyState = await service.ReadAsync(mergeOnly);
        var mergeOnlyRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(mergeOnly, mergeOnlyState, "origin", "merge-only-branch"));
        check(!mergeOnlyState.CanPublish && mergeOnlyState.HasUpstreamConfiguration && mergeOnlyRejected
            && (await git.RunAsync(mergeOnly, "config", "--get", "branch.main.merge")).Trim() == "refs/heads/manual-target",
            "Один только branch.merge считается существующей настройкой и не перезаписывается");
    }

    private static async Task CheckFailedPublishAsync(
        string root,
        GitService git,
        GitRemoteService service,
        Action<bool, string> check)
    {
        var repository = Path.Combine(root, "failed-publish-client");
        var invalidRemote = Path.Combine(root, "not-a-repository");
        Directory.CreateDirectory(invalidRemote);
        await BranchPublicationChecks.InitializeClientAsync(git, repository);
        await git.RunAsync(repository, "remote", "add", "origin", invalidRemote);
        var state = await service.ReadAsync(repository);
        var configBefore = await BranchPublicationChecks.ReadBranchConfigAsync(git, repository);
        var failed = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(repository, state, "origin", "failed-branch"));
        check(failed && (await BranchPublicationChecks.ReadBranchConfigAsync(git, repository)) == configBefore
            && await BranchPublicationChecks.ReferenceExistsAsync(git, invalidRemote, "refs/heads/failed-branch") == false,
            "Ошибка удалённого сервера не настраивает upstream и не оставляет новую ветку");
    }

    private static async Task CheckMultiplePushUrlsAsync(
        string root,
        string firstBare,
        string secondBare,
        GitService git,
        GitRemoteService service,
        Action<bool, string> check)
    {
        var distinctUrlRepository = Path.Combine(root, "distinct-url-client");
        await BranchPublicationChecks.InitializeClientAsync(git, distinctUrlRepository);
        await git.RunAsync(distinctUrlRepository, "remote", "add", "origin", firstBare);
        await git.RunAsync(distinctUrlRepository, "remote", "set-url", "--push", "origin", secondBare);
        var distinctUrlState = await service.ReadAsync(distinctUrlRepository);
        var distinctUrlRejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(distinctUrlRepository, distinctUrlState, "origin", "different-fetch-push"));
        check(distinctUrlState.CanPublish && distinctUrlRejected
            && await BranchPublicationChecks.ReferenceExistsAsync(git, firstBare, "refs/heads/different-fetch-push") == false
            && await BranchPublicationChecks.ReferenceExistsAsync(git, secondBare, "refs/heads/different-fetch-push") == false,
            "Разные адреса Fetch и Push не настраивают неправильный tracking ref");

        var repository = Path.Combine(root, "multiple-push-urls-client");
        await BranchPublicationChecks.InitializeClientAsync(git, repository);
        await git.RunAsync(repository, "remote", "add", "origin", firstBare);
        await git.RunAsync(repository, "remote", "set-url", "--add", "--push", "origin", firstBare);
        await git.RunAsync(repository, "remote", "set-url", "--add", "--push", "origin", secondBare);
        var state = await service.ReadAsync(repository);
        var rejected = await BranchPublicationChecks.RejectedAsync(
            () => service.PublishAsync(repository, state, "origin", "multiple-url-branch"));
        check(!state.CanPublish && state.PublicationUnavailableReason is not null && rejected
            && await BranchPublicationChecks.ReferenceExistsAsync(git, firstBare, "refs/heads/multiple-url-branch") == false
            && await BranchPublicationChecks.ReferenceExistsAsync(git, secondBare, "refs/heads/multiple-url-branch") == false,
            "Несколько Push URL блокируют публикацию и не меняют ни один remote");
    }

    private static async Task InitializeClientAsync(GitService git, string repository, bool createCommit = true)
    {
        Directory.CreateDirectory(repository);
        await git.RunAsync(repository, "init", "--initial-branch=main", "-q");
        await git.RunAsync(repository, "config", "user.name", "Branch Publication Checks");
        await git.RunAsync(repository, "config", "user.email", "branch-publication@example.invalid");
        await git.RunAsync(repository, "config", "core.autocrlf", "false");
        if (!createCommit)
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "baseline\n", new UTF8Encoding(false));
        await git.RunAsync(repository, "add", "--all");
        await git.RunAsync(repository, "commit", "-m", "baseline");
    }

    private static string ToGitBashPath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (normalized.Length > 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            return $"/{char.ToLowerInvariant(normalized[0])}{normalized[2..]}";
        }

        return normalized;
    }

    private static async Task<string> ReadBranchConfigAsync(GitService git, string repository)
    {
        try
        {
            return await git.RunAsync(repository, "config", "--local", "--get-regexp", "^branch\\.");
        }
        catch (GitException)
        {
            return "";
        }
    }

    private static async Task<bool> ReferenceExistsAsync(GitService git, string repository, string reference)
    {
        try
        {
            await git.RunAsync(repository, "show-ref", "--verify", "--quiet", reference);
            return true;
        }
        catch (GitException)
        {
            return false;
        }
    }

    private static async Task<bool> RejectedAsync(Func<Task<RemoteOperationResult>> operation)
    {
        try
        {
            await operation();
            return false;
        }
        catch (GitException)
        {
            return true;
        }
    }
}
