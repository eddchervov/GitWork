using GitWork.Core;

public static class RemoteChecks
{
    public static async Task RunAsync(string source, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-remote-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var git = new GitService();
            var service = new GitRemoteService(git);
            var remote = Path.Combine(root, "remote.git");
            var client = Path.Combine(root, "client");
            await git.RunAsync(root, "clone", "--bare", "--no-hardlinks", "--", Path.GetFullPath(source), remote);
            await git.RunAsync(root, "clone", "--no-hardlinks", "--", remote, client);
            var branch = (await git.RunAsync(client, "branch", "--show-current")).Trim();
            var head = (await git.RunAsync(client, "rev-parse", "HEAD")).Trim();
            var older = (await git.RunAsync(client, "rev-parse", "HEAD^")).Trim();
            await git.RunAsync(client, "switch", "-c", "sync-check", older);
            await git.RunAsync(client, "config", "branch.sync-check.remote", "origin");
            await git.RunAsync(client, "config", "branch.sync-check.merge", "refs/heads/" + branch);
            await git.RunAsync(client, "update-ref", "refs/remotes/origin/" + branch, older);
            var marker = Path.Combine(client, "remote-check-untracked.txt");
            await File.WriteAllTextAsync(marker, "Локальные данные");
            var dirtyState = await service.ReadAsync(client);
            check(!dirtyState.CanPull && !dirtyState.CanPush
                && dirtyState.PullUnavailableReason?.Contains("изменения", StringComparison.OrdinalIgnoreCase) == true,
                "Pull блокируется незакоммиченными изменениями, а Push — отставанием от upstream");
            var fetch = await service.FetchAsync(client, "origin");
            var tracking = (await git.RunAsync(client, "rev-parse", "refs/remotes/origin/" + branch)).Trim();
            check(fetch.Success && fetch.FetchCompleted && tracking == head && fetch.BeforeHead == fetch.AfterHead
                && await File.ReadAllTextAsync(marker) == "Локальные данные", "Fetch обновляет удалённые ветки и сохраняет HEAD и рабочие файлы");
            var blocked = false;
            try
            {
                await service.PullAsync(client);
            }
            catch (GitException)
            {
                blocked = true;
            }

            check(blocked && (await git.RunAsync(client, "rev-parse", "HEAD")).Trim() == older,
                "Грязная копия не изменяется при попытке Pull");
            File.Delete(marker);
            var ready = await service.ReadAsync(client);
            check(ready.CanPull && ready.Behind > 0 && ready.Ahead == 0, "Перед Pull показано отставание от upstream");
            await git.RunAsync(remote, "update-ref", "-d", "refs/heads/" + branch);
            var deletedUpstreamBlocked = false;
            try
            {
                await service.PullAsync(client);
            }
            catch (GitException)
            {
                deletedUpstreamBlocked = true;
            }

            check(deletedUpstreamBlocked && (await git.RunAsync(client, "rev-parse", "HEAD")).Trim() == older,
                "Удалённая upstream-ветка не заменяется устаревшей tracking-ссылкой при Pull");
            await git.RunAsync(remote, "update-ref", "refs/heads/" + branch, head);
            var pull = await service.PullAsync(client);
            check(pull.Success && pull.PullCompleted && pull.AfterHead == head && (await git.GetStatusAsync(client, true)).Count == 0,
                "Pull выполняет fast-forward до существующего коммита без нового коммита");
            var again = await service.PullAsync(client);
            check(again.Success && again.BeforeHead == again.AfterHead, "Повторный Pull актуальной ветки ничего не меняет");

            await git.RunAsync(client, "switch", "--detach", older);
            var detachedState = await service.ReadAsync(client);
            check(!detachedState.CanPull && !detachedState.CanPush, "Detached HEAD запрещает Pull и Push");
            check((await service.FetchAsync(client, "origin")).Success, "Fetch доступен в detached HEAD");
            await git.RunAsync(client, "switch", "-c", "no-upstream", head);
            var noUpstreamState = await service.ReadAsync(client);
            check(!noUpstreamState.CanPull && !noUpstreamState.CanPush, "Ветка без upstream блокирует Pull и Push");
            await git.RunAsync(client, "switch", "sync-check");
            var mergeMarker = (await git.RunAsync(client, "rev-parse", "--git-path", "MERGE_HEAD")).Trim();
            var mergePath = Path.GetFullPath(mergeMarker, client);
            await File.WriteAllTextAsync(mergePath, older + "\n");
            check(!(await service.ReadAsync(client)).CanPull, "Незавершённое слияние блокирует Pull");
            File.Delete(mergePath);

            await git.RunAsync(client, "switch", "-c", "custom-mapping", older);
            await git.RunAsync(client, "config", "branch.custom-mapping.remote", "origin");
            await git.RunAsync(client, "config", "branch.custom-mapping.merge", "refs/heads/" + branch);
            await git.RunAsync(client, "config", "--replace-all", "remote.origin.fetch", $"+refs/heads/{branch}:refs/remotes/origin/renamed-tracking");
            await git.RunAsync(client, "update-ref", "refs/remotes/origin/renamed-tracking", head);
            check((await service.ReadAsync(client)).UpstreamBranch == branch && (await service.PullAsync(client)).AfterHead == head,
                "Pull использует исходную remote-ветку при другом имени tracking-ссылки");

            var details = new GitCommitDetailsService(git);
            var firstCommit = (await git.RunAsync(client, "rev-list", "--max-parents=0", "HEAD")).Trim().Split('\n')[0];
            var files = await details.GetFilesAsync(client, firstCommit);
            var diff = await details.GetDiffAsync(client, firstCommit, files.First());
            check(files.Count > 0 && (diff.Lines.Count > 0 || diff.IsBinary || diff.Message is not null),
                "Граф открывает файлы и diff первого коммита");
            var latestFiles = await details.GetFilesAsync(client, head);
            check(latestFiles.Count > 0, "Граф читает изменения обычного коммита относительно родителя");

            var pushTarget = Path.Combine(root, "push-target.git");
            var alternatePushTarget = Path.Combine(root, "alternate-push-target.git");
            var multiplePushTarget = Path.Combine(root, "multiple-push-target.git");
            await git.RunAsync(root, "clone", "--bare", "--no-hardlinks", "--", remote, pushTarget);
            await git.RunAsync(root, "clone", "--bare", "--no-hardlinks", "--", remote, alternatePushTarget);
            await git.RunAsync(root, "clone", "--bare", "--no-hardlinks", "--", remote, multiplePushTarget);
            await git.RunAsync(remote, "update-ref", "refs/heads/push-check", head);
            await git.RunAsync(pushTarget, "update-ref", "refs/heads/push-check", head);
            await git.RunAsync(alternatePushTarget, "update-ref", "refs/heads/push-check", head);
            await git.RunAsync(multiplePushTarget, "update-ref", "refs/heads/push-check", head);
            await git.RunAsync(client, "update-ref", "refs/remotes/origin/push-check", head);
            await git.RunAsync(client, "remote", "set-url", "--push", "origin", pushTarget);
            await git.RunAsync(client, "switch", "-c", "push-check", head);
            await git.RunAsync(client, "config", "branch.push-check.remote", "origin");
            await git.RunAsync(client, "config", "branch.push-check.merge", "refs/heads/push-check");
            await git.RunAsync(client, "-c", "user.name=GitWork checks", "-c", "user.email=checks@example.invalid",
                "commit", "--allow-empty", "-m", "First local push commit");
            var stalePushState = await service.ReadAsync(client);
            var originInfo = stalePushState.Remotes.Single(item => item.Name == "origin");
            check(stalePushState.CanPush && stalePushState.Ahead == 1
                && originInfo.Url == remote && originInfo.PushUrl == pushTarget,
                "Локальный коммит доступен для Push в настроенный pushurl, отличный от fetch URL");
            await git.RunAsync(client, "-c", "user.name=GitWork checks", "-c", "user.email=checks@example.invalid",
                "commit", "--allow-empty", "-m", "Second local push commit");
            var staleHeadRejected = false;
            try
            {
                await service.PushAsync(client, stalePushState);
            }
            catch (GitException)
            {
                staleHeadRejected = true;
            }

            check(staleHeadRejected && (await git.RunAsync(remote, "rev-parse", "refs/heads/push-check")).Trim() == head
                && (await git.RunAsync(pushTarget, "rev-parse", "refs/heads/push-check")).Trim() == head,
                "Push отклоняется, если HEAD изменился после отображения состояния");

            await git.RunAsync(client, "remote", "set-url", "--add", "--push", "origin", multiplePushTarget);
            var multiplePushState = await service.ReadAsync(client);
            var multiplePushRejected = false;
            try
            {
                await service.PushAsync(client, multiplePushState);
            }
            catch (GitException)
            {
                multiplePushRejected = true;
            }

            check(!multiplePushState.CanPush && multiplePushState.PushUnavailableReason?.Contains("единственный", StringComparison.OrdinalIgnoreCase) == true
                && multiplePushRejected
                && (await git.RunAsync(pushTarget, "rev-parse", "refs/heads/push-check")).Trim() == head
                && (await git.RunAsync(multiplePushTarget, "rev-parse", "refs/heads/push-check")).Trim() == head,
                "Несколько pushurl блокируют Push, не меняя удалённые ветки");
            await git.RunAsync(client, "remote", "set-url", "--delete", "--push", "origin", multiplePushTarget);

            var expectedPushUrlState = await service.ReadAsync(client);
            await git.RunAsync(client, "remote", "set-url", "--push", "origin", alternatePushTarget);
            var changedPushUrlRejected = false;
            try
            {
                await service.PushAsync(client, expectedPushUrlState);
            }
            catch (GitException)
            {
                changedPushUrlRejected = true;
            }

            check(changedPushUrlRejected
                && (await git.RunAsync(pushTarget, "rev-parse", "refs/heads/push-check")).Trim() == head
                && (await git.RunAsync(alternatePushTarget, "rev-parse", "refs/heads/push-check")).Trim() == head,
                "Изменение pushurl после показа состояния блокирует отправку на оба сервера");
            await git.RunAsync(client, "remote", "set-url", "--push", "origin", pushTarget);

            var pushState = await service.ReadAsync(client);
            var push = await service.PushAsync(client, pushState);
            var pushedHead = (await git.RunAsync(client, "rev-parse", "HEAD")).Trim();
            var stateAfterDistinctPush = await service.ReadAsync(client);
            check(push.Success && (await git.RunAsync(pushTarget, "rev-parse", "refs/heads/push-check")).Trim() == pushedHead
                && (await git.RunAsync(remote, "rev-parse", "refs/heads/push-check")).Trim() == head
                && stateAfterDistinctPush.Ahead == pushState.Ahead,
                "Push отправляет SHA HEAD в upstream pushurl, не меняя fetch-сервер или его tracking-ссылку");
            await git.RunAsync(remote, "update-ref", "refs/heads/push-check", pushedHead);
            await service.FetchAsync(client, "origin");
            check((await service.ReadAsync(client)).Ahead == 0,
                "Fetch обновляет tracking-ссылку до коммита, отправленного на другой pushurl");

            var competing = Path.Combine(root, "competing-client");
            await git.RunAsync(root, "clone", "--no-hardlinks", "--", pushTarget, competing);
            await git.RunAsync(competing, "switch", "--track", "-c", "push-check", "origin/push-check");
            await git.RunAsync(competing, "-c", "user.name=GitWork checks", "-c", "user.email=checks@example.invalid",
                "commit", "--allow-empty", "-m", "Concurrent remote push commit");
            await git.RunAsync(competing, "push", "origin", "HEAD:refs/heads/push-check");
            var competingHead = (await git.RunAsync(competing, "rev-parse", "HEAD")).Trim();
            await git.RunAsync(remote, "update-ref", "refs/heads/push-check", competingHead);
            await git.RunAsync(client, "-c", "user.name=GitWork checks", "-c", "user.email=checks@example.invalid",
                "commit", "--allow-empty", "-m", "Local push after remote advance");
            var nonFastForwardState = await service.ReadAsync(client);
            var nonFastForwardRejected = false;
            try
            {
                await service.PushAsync(client, nonFastForwardState);
            }
            catch (GitException)
            {
                nonFastForwardRejected = true;
            }

            check(nonFastForwardRejected, "Git отклоняет Push, если удалённая ветка продвинулась параллельно");
            await service.FetchAsync(client, "origin");
            var divergedState = await service.ReadAsync(client);
            check(!divergedState.CanPush && divergedState.Behind > 0,
                "Расхождение веток блокирует Push до получения актуального состояния");

            await git.RunAsync(remote, "update-ref", "refs/heads/same-push-check", head);
            await git.RunAsync(client, "update-ref", "refs/remotes/origin/same-push-check", head);
            await git.RunAsync(client, "switch", "-c", "same-push-check", head);
            await git.RunAsync(client, "config", "branch.same-push-check.remote", "origin");
            await git.RunAsync(client, "config", "branch.same-push-check.merge", "refs/heads/same-push-check");
            await git.RunAsync(client, "remote", "set-url", "--push", "origin", remote);
            await git.RunAsync(client, "-c", "user.name=GitWork checks", "-c", "user.email=checks@example.invalid",
                "commit", "--allow-empty", "-m", "Push to shared fetch target");
            var sameTargetState = await service.ReadAsync(client);
            var sameTargetPush = await service.PushAsync(client, sameTargetState);
            var sameTargetHead = (await git.RunAsync(client, "rev-parse", "HEAD")).Trim();
            check(sameTargetPush.Success && (await service.ReadAsync(client)).Ahead == 0
                && (await git.RunAsync(remote, "rev-parse", "refs/heads/same-push-check")).Trim() == sameTargetHead,
                "Push в единственный совпадающий fetch URL обновляет tracking-ссылку без отставшего счётчика");

            await git.RunAsync(client, "switch", "--orphan", "empty-head");
            var orphanRows = await new GitGraphService(git).GetAsync(client);
            check(orphanRows.Count > 0 && orphanRows.All(row => !row.Commit.IsHead),
                "Пустая текущая ветка не скрывает историю остальных веток");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("GitWork-remote-checks-", StringComparison.Ordinal))
            {
                foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(resolved, true);
            }
        }
    }
}
