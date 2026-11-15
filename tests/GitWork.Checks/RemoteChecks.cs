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
            check(!dirtyState.CanPull && dirtyState.PullUnavailableReason?.Contains("изменения", StringComparison.OrdinalIgnoreCase) == true,
                "Pull недоступен при локальных изменениях");
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
            check(!(await service.ReadAsync(client)).CanPull, "Detached HEAD запрещает Pull");
            check((await service.FetchAsync(client, "origin")).Success, "Fetch доступен в detached HEAD");
            await git.RunAsync(client, "switch", "-c", "no-upstream", head);
            check(!(await service.ReadAsync(client)).CanPull, "Ветка без upstream объясняет недоступность Pull");
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
