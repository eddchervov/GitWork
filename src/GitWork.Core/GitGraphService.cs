namespace GitWork.Core;

public sealed class GitGraphService(GitService git)
{
    public const int MaximumCommitCount = 1000;
    public const int MaximumOutputLength = 12_000_000;
    public const int MaximumSideCommitCount = 100;
    public const int MaximumExpandedMerges = 8;
    private readonly GitService _git = git;

    public async Task<IReadOnlyList<GitGraphRow>> GetAsync(string worktree, int limit = 200)
    {
        if (limit is < 1 or > GitGraphService.MaximumCommitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Количество коммитов должно быть от 1 до 1000.");
        }

        string headHash;
        try
        {
            headHash = (await this._git.RunAsync(worktree, "rev-parse", "--verify", "--quiet", "HEAD")).Trim();
        }
        catch (GitException exception) when (string.IsNullOrWhiteSpace(exception.Message))
        {
            headHash = "";
        }

        var commitsOutput = await this._git.RunLimitedAsync(
            worktree,
            GitGraphService.MaximumOutputLength + 1,
            ["log",
            "--all",
            "--topo-order",
            $"--max-count={limit}",
            "--format=%H%x00%P%x00%an%x00%aI%x00%s%x00",
            .. headHash.Length > 0 ? new[] { "HEAD" } : []]);
        if (commitsOutput.Length > GitGraphService.MaximumOutputLength)
        {
            throw new GitException("История слишком велика для просмотра за один раз. Уменьшите число коммитов.");
        }

        var referenceOutput = await this._git.RunLimitedAsync(
            worktree,
            2_000_001,
            "for-each-ref",
            "--format=%(refname)%00%(objectname)%00%(*objectname)%00",
            "refs/heads",
            "refs/remotes",
            "refs/tags");
        if (referenceOutput.Length > 2_000_000)
        {
            throw new GitException("В репозитории слишком много ссылок для построения графа.");
        }

        var references = GitGraphService.ParseReferences(referenceOutput);
        var commits = GitGraphService.ParseCommits(commitsOutput, references, headHash);

        return GitGraphService.BuildLayout(commits);
    }

    public async Task<IReadOnlyList<GitGraphRow>> GetCompactAsync(
        string worktree,
        int limit = 200,
        IReadOnlySet<string>? expandedMerges = null)
    {
        if (limit is < 1 or > GitGraphService.MaximumCommitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Количество коммитов должно быть от 1 до 1000.");
        }

        if (expandedMerges?.Count > GitGraphService.MaximumExpandedMerges)
        {
            throw new GitException("Одновременно можно раскрыть не более восьми merge-коммитов.");
        }

        var selectedMerges = expandedMerges is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : expandedMerges.Where(GitGraphService.IsObjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedMerges.Count > GitGraphService.MaximumExpandedMerges)
        {
            throw new GitException("Одновременно можно раскрыть не более восьми merge-коммитов.");
        }

        string headHash;
        try
        {
            headHash = (await this._git.RunAsync(worktree, "rev-parse", "--verify", "--quiet", "HEAD")).Trim();
        }
        catch (GitException exception) when (string.IsNullOrWhiteSpace(exception.Message))
        {
            headHash = "";
        }

        var referenceOutput = await this._git.RunLimitedAsync(
            worktree,
            2_000_001,
            "for-each-ref",
            "--format=%(refname)%00%(objectname)%00%(*objectname)%00",
            "refs/heads",
            "refs/remotes",
            "refs/tags");
        if (referenceOutput.Length > 2_000_000)
        {
            throw new GitException("В репозитории слишком много ссылок для построения графа.");
        }

        var references = GitGraphService.ParseReferences(referenceOutput);
        if (headHash.Length == 0)
        {
            return [];
        }

        var baseOutput = await this.ReadLogAsync(worktree, limit, "--first-parent", "HEAD");
        var baseCommits = GitGraphService.ParseCommits(baseOutput, references, headHash);
        var sideCommits = new Dictionary<string, GitGraphCommit>(StringComparer.OrdinalIgnoreCase);
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingMerges = new Queue<GitGraphCommit>(baseCommits.Where(commit => selectedMerges.Contains(commit.Hash)));

        while (pendingMerges.Count > 0)
        {
            var merge = pendingMerges.Dequeue();
            if (!expanded.Add(merge.Hash) || !GitGraphService.IsObjectId(merge.Hash) || merge.Parents.Count < 2)
            {
                continue;
            }

            var remaining = GitGraphService.MaximumSideCommitCount;

            for (var parentIndex = 1; parentIndex < merge.Parents.Count && remaining > 0; parentIndex++)
            {
                var secondaryParent = merge.Parents[parentIndex];
                var output = await this.ReadLogAsync(
                    worktree,
                    remaining,
                    "--topo-order",
                    "--first-parent",
                    secondaryParent,
                    "--not",
                    merge.Parents[0]);
                var parsedCommits = GitGraphService.ParseCommits(output, references, headHash);
                remaining -= parsedCommits.Count;
                foreach (var commit in parsedCommits)
                {
                    if (baseCommits.Any(item => string.Equals(item.Hash, commit.Hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    sideCommits.TryAdd(commit.Hash, commit);
                    if (selectedMerges.Contains(commit.Hash))
                    {
                        pendingMerges.Enqueue(commit);
                    }
                }

            }
        }

        return GitGraphService.BuildCompactLayout(baseCommits, sideCommits.Values.ToArray(), selectedMerges);
    }

    private async Task<string> ReadLogAsync(string worktree, int limit, params string[] revisions)
    {
        var output = await this._git.RunLimitedAsync(
            worktree,
            GitGraphService.MaximumOutputLength + 1,
            ["log",
            "--topo-order",
            $"--max-count={limit}",
            "--format=%H%x00%P%x00%an%x00%aI%x00%s%x00",
            .. revisions]);
        if (output.Length > GitGraphService.MaximumOutputLength)
        {
            throw new GitException("История слишком велика для просмотра за один раз. Уменьшите число коммитов.");
        }

        return output;
    }

    public static IReadOnlyList<GitGraphCommit> ParseCommits(
        string output,
        IReadOnlyDictionary<string, IReadOnlyList<string>> references,
        string headHash)
    {
        var fields = output.Split('\0');
        var commits = new List<GitGraphCommit>();

        for (var index = 0; index + 4 < fields.Length; index += 5)
        {
            var hash = fields[index].TrimStart('\r', '\n');
            var parents = fields[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var author = fields[index + 2].TrimEnd('\r', '\n');
            var date = fields[index + 3].TrimEnd('\r', '\n');
            var subject = fields[index + 4].TrimEnd('\r', '\n');

            if (!GitGraphService.IsObjectId(hash))
            {
                continue;
            }

            var labels = references.GetValueOrDefault(hash) ?? [];
            if (string.Equals(hash, headHash, StringComparison.OrdinalIgnoreCase))
            {
                labels = [.. labels, "HEAD"];
            }

            commits.Add(new GitGraphCommit(
                hash,
                hash[..Math.Min(7, hash.Length)],
                subject,
                author,
                date,
                parents,
                labels,
                string.Equals(hash, headHash, StringComparison.OrdinalIgnoreCase)));
        }

        return commits;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseReferences(string output)
    {
        var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length < 3)
            {
                continue;
            }

            var reference = fields[0].TrimStart('\r', '\n').TrimEnd('\r', '\n');
            var directHash = fields[1].TrimEnd('\r', '\n');
            var peeledHash = fields[2].TrimEnd('\r', '\n');
            var objectHash = GitGraphService.IsObjectId(peeledHash) ? peeledHash : directHash;

            if (!GitGraphService.IsObjectId(objectHash) || reference.Length == 0)
            {
                continue;
            }

            if (!references.TryGetValue(objectHash, out var labels))
            {
                labels = [];
                references[objectHash] = labels;
            }

            labels.Add(reference);
        }

        return references.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<GitGraphRow> BuildLayout(IReadOnlyList<GitGraphCommit> commits)
    {
        return GitGraphService.BuildLayout(commits, commits.Select(commit => commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<GitGraphRow> BuildCompactLayout(
        IReadOnlyList<GitGraphCommit> firstParentCommits,
        IReadOnlyList<GitGraphCommit> expandedHistory,
        IReadOnlySet<string> expandedMerges)
    {
        var expandedHashes = expandedMerges.Where(GitGraphService.IsObjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commits = firstParentCommits.Concat(expandedHistory)
            .GroupBy(commit => commit.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(commit => commit.Hash, StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        if (firstParentCommits.Count > 0)
        {
            pending.Push(firstParentCommits[0].Hash);
        }

        while (pending.TryPop(out var hash))
        {
            if (!reachable.Add(hash) || !commits.TryGetValue(hash, out var commit))
            {
                continue;
            }

            var visibleParents = expandedHashes.Contains(hash) ? commit.Parents : commit.Parents.Take(1);
            foreach (var parent in visibleParents)
            {
                if (commits.ContainsKey(parent))
                {
                    pending.Push(parent);
                }
            }
        }

        commits = commits.Where(pair => reachable.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var ordered = GitGraphService.TopologicalOrder(commits, firstParentCommits);
        var visibleHashes = ordered.Select(commit => commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayCommits = ordered.Select(commit =>
        {
            var isExpanded = expandedHashes.Contains(commit.Hash);
            var parents = isExpanded ? commit.Parents : commit.Parents.Take(1).ToArray();
            parents = parents.Where(parent => visibleHashes.Contains(parent)).ToArray();
            return commit with { DisplayParents = parents };
        }).ToArray();

        var rows = GitGraphService.BuildLayout(displayCommits, visibleHashes);
        return rows.Select(row => row with
        {
            HasContinuation = row.Commit.Parents.Count > 0
                && (!visibleHashes.Contains(row.Commit.Parents[0])
                    || expandedHashes.Contains(row.Commit.Hash)
                        && row.Commit.Parents.Skip(1).Any(parent => !visibleHashes.Contains(parent))),
            HasMoreMainline = firstParentCommits.Count > 0
                && string.Equals(row.Commit.Hash, firstParentCommits[^1].Hash, StringComparison.OrdinalIgnoreCase)
                && row.Commit.Parents.Count > 0
                && !visibleHashes.Contains(row.Commit.Parents[0])
        }).ToArray();
    }

    private static IReadOnlyList<GitGraphCommit> TopologicalOrder(
        IReadOnlyDictionary<string, GitGraphCommit> commits,
        IReadOnlyList<GitGraphCommit> firstParentCommits)
    {
        var childrenLeft = commits.Keys.ToDictionary(hash => hash, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var commit in commits.Values)
        {
            foreach (var parent in commit.Parents)
            {
                if (childrenLeft.ContainsKey(parent))
                {
                    childrenLeft[parent]++;
                }
            }
        }

        var priority = firstParentCommits.Select((commit, index) => (commit.Hash, index))
            .ToDictionary(item => item.Hash, item => item.index, StringComparer.OrdinalIgnoreCase);
        var ready = new PriorityQueue<string, int>();
        foreach (var (hash, childCount) in childrenLeft)
        {
            if (childCount == 0)
            {
                ready.Enqueue(hash, priority.GetValueOrDefault(hash, int.MaxValue));
            }
        }

        var ordered = new List<GitGraphCommit>(commits.Count);
        while (ready.TryDequeue(out var hash, out _))
        {
            var commit = commits[hash];
            ordered.Add(commit);
            foreach (var parent in commit.Parents)
            {
                if (!childrenLeft.TryGetValue(parent, out var count))
                {
                    continue;
                }

                childrenLeft[parent] = --count;
                if (count == 0)
                {
                    ready.Enqueue(parent, priority.GetValueOrDefault(parent, int.MaxValue));
                }
            }
        }

        return ordered;
    }

    private static IReadOnlyList<GitGraphRow> BuildLayout(
        IReadOnlyList<GitGraphCommit> commits,
        IReadOnlySet<string> visibleHashes)
    {
        var rows = new List<GitGraphRow>(commits.Count);
        var lanes = new List<Lane>();

        foreach (var commit in commits)
        {
            var topCount = lanes.Count;
            var oldLanes = lanes.ToArray();
            var nodeLane = lanes.FindIndex(lane => string.Equals(lane.CommitHash, commit.Hash, StringComparison.OrdinalIgnoreCase));

            if (nodeLane < 0)
            {
                nodeLane = 0;
                lanes.Insert(0, new Lane(commit.Hash, GitGraphService.GetColorIndex(commit.Hash)));
            }

            var nodeLaneAtCenter = nodeLane;
            var nodeColor = lanes[nodeLane].ColorIndex;
            var displayParents = (commit.DisplayParents ?? commit.Parents)
                .Where(visibleHashes.Contains)
                .ToArray();
            if (displayParents.Length == 0)
            {
                lanes.RemoveAt(nodeLane);
            }
            else
            {
                var primaryParent = displayParents[0];
                var existingPrimary = lanes.FindIndex(lane => string.Equals(lane.CommitHash, primaryParent, StringComparison.OrdinalIgnoreCase));
                if (existingPrimary >= 0 && existingPrimary != nodeLane)
                {
                    lanes.RemoveAt(nodeLane);
                }
                else
                {
                    lanes[nodeLane] = new Lane(primaryParent, nodeColor);
                }

                foreach (var parent in displayParents.Skip(1))
                {
                    if (lanes.Any(lane => string.Equals(lane.CommitHash, parent, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var primaryIndex = lanes.FindIndex(lane => string.Equals(lane.CommitHash, primaryParent, StringComparison.OrdinalIgnoreCase));
                    lanes.Insert(Math.Min(primaryIndex + 1, lanes.Count), new Lane(parent, GitGraphService.GetColorIndex(parent)));
                }
            }

            var lines = new List<GitGraphLine>();
            for (var laneIndex = 0; laneIndex < oldLanes.Length; laneIndex++)
            {
                var oldLane = oldLanes[laneIndex];
                if (string.Equals(oldLane.CommitHash, commit.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(new GitGraphLine(laneIndex, 0, nodeLaneAtCenter, 0.5, oldLane.ColorIndex));
                    continue;
                }

                var destination = lanes.FindIndex(lane => string.Equals(lane.CommitHash, oldLane.CommitHash, StringComparison.OrdinalIgnoreCase));
                if (destination >= 0)
                {
                    lines.Add(new GitGraphLine(laneIndex, 0, destination, 1, oldLane.ColorIndex));
                }
            }

            foreach (var parent in displayParents.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var destination = lanes.FindIndex(lane => string.Equals(lane.CommitHash, parent, StringComparison.OrdinalIgnoreCase));
                if (destination >= 0)
                {
                    lines.Add(new GitGraphLine(nodeLaneAtCenter, 0.5, destination, 1, lanes[destination].ColorIndex));
                }
            }

            rows.Add(new GitGraphRow(commit, topCount, nodeLaneAtCenter, nodeColor, lanes.Count, lines));
        }

        return rows;
    }

    private static bool IsObjectId(string value) => value.Length is 40 or 64
        && value.All(Uri.IsHexDigit);

    public static int GetColorIndex(string hash)
    {
        var value = hash.Length >= 8 ? hash[..8] : hash;
        return (int)(Convert.ToUInt32(value, 16) % 8);
    }

    private sealed record Lane(string CommitHash, int ColorIndex);
}
