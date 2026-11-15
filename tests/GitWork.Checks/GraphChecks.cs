using GitWork.Core;

public static class GraphChecks
{
    public static async Task RunAsync(string source, Action<bool, string> check)
    {
        var merge = GraphChecks.Commit("a000000", ["b000000", "c000000"]);
        var firstBranch = GraphChecks.Commit("b000000", ["d000000"]);
        var secondBranch = GraphChecks.Commit("c000000", ["d000000"]);
        var shared = GraphChecks.Commit("d000000", ["e000000"]);
        var root = GraphChecks.Commit("e000000", []);
        var rows = GitGraphService.BuildLayout([merge, firstBranch, secondBranch, shared, root]);
        check(rows.Count == 5 && rows[0].Commit.Hash == merge.Hash, "Граф размещает новые коммиты раньше родителей");
        check(rows[0].BottomLaneCount == 2, "Родители merge-коммита получают отдельные цветные дорожки");
        check(rows[2].BottomLaneCount == 1, "Сходящиеся ветки объединяются на общей дорожке");
        check(rows.All(row => row.NodeLane >= 0 && row.NodeLane < Math.Max(1, row.TopLaneCount + 1)
            && row.Lines.All(line => line.FromLane >= 0 && line.ToLane >= 0)), "Линии DAG остаются в пределах дорожек");
        var firstParentLine = rows[0].Lines.First(line => line.FromY == 0.5 && line.ToLane == 0);
        check(GraphChecks.NodeColor(rows[1]) == firstParentLine.ColorIndex, "Основная ветка сохраняет свой цвет при переходе к родителю");

        var octopusRows = GitGraphService.BuildLayout(
        [
            GraphChecks.Commit("1000000", ["2000000", "3000000", "4000000", "5000000"]),
            GraphChecks.Commit("2000000", []),
            GraphChecks.Commit("3000000", []),
            GraphChecks.Commit("4000000", []),
            GraphChecks.Commit("5000000", [])
        ]);
        check(octopusRows[0].BottomLaneCount == 4 && octopusRows[0].Lines.Count == 4,
            "Octopus merge отображает все четыре родительские линии");

        var parsedHead = "a" + new string('0', 39);
        var parsedParent = "b" + new string('0', 39);
        var parsed = GitGraphService.ParseCommits(
            parsedHead + "\0" + parsedParent + "\0Имя автора\0" +
            "2026-11-15T12:00:00+07:00\0Тема коммита\0",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [parsedHead] = ["refs/heads/main"]
            },
            parsedHead);
        check(parsed.Count == 1 && parsed[0].IsHead && parsed[0].References.Contains("HEAD") && parsed[0].Subject == "Тема коммита",
            "Парсер сохраняет HEAD, тему и кириллического автора");
        check(GitGraphService.ParseCommits("", new Dictionary<string, IReadOnlyList<string>>(), "").Count == 0,
            "Пустая история разбирается безопасно");

        var references = GitGraphService.ParseReferences(
            "refs/heads/main\0" + parsedHead + "\0\nrefs/tags/v1\0tag00000000000000000000000000000000000000\0" + parsedHead + "\0\n");
        check(references[parsedHead].SequenceEqual(["refs/heads/main", "refs/tags/v1"]),
            "Ссылки включают ветки и разыменованные теги");

        var emptyRepository = Path.Combine(Path.GetTempPath(), "GitWork-graph-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRepository);
        try
        {
            var git = new GitService();
            await git.RunAsync(emptyRepository, "init", "--initial-branch=main");
            var emptyRows = await new GitGraphService(git).GetAsync(emptyRepository);
            check(emptyRows.Count == 0, "Новая ветка без первого коммита показывает пустой граф");
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(emptyRepository);
            if (resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot).StartsWith("GitWork-graph-empty-", StringComparison.Ordinal))
            {
                Directory.Delete(resolvedRoot, true);
            }
        }

        if (Directory.Exists(source))
        {
            var actualRows = await new GitGraphService(new GitService()).GetAsync(source, 50);
            check(actualRows.Count is > 0 and <= 50 && actualRows.Any(row => row.Commit.IsHead), "Чтение истории локального репозитория без изменений");
        }
    }

    private static GitGraphCommit Commit(string id, IReadOnlyList<string> parents) =>
        new(id.PadRight(40, '0'), id, id, "Автор", "2026-11-15T12:00:00+07:00", parents.Select(parent => parent.PadRight(40, '0')).ToArray(), [], false);

    private static int NodeColor(GitGraphRow row) => row.NodeColorIndex;
}
