using GitWork.Core;

public static class CompactGraphChecks
{
    public static void Run(Action<bool, string> check)
    {
        var merge = CompactGraphChecks.Commit("a000000", ["b000000", "c000000"]);
        var mainline = CompactGraphChecks.Commit("b000000", ["d000000"]);
        var side = CompactGraphChecks.Commit("c000000", ["d000000"]);
        var orphan = CompactGraphChecks.Commit("f000000", []);
        var shared = CompactGraphChecks.Commit("d000000", ["e000000"]);
        var root = CompactGraphChecks.Commit("e000000", []);
        var collapsed = GitGraphService.BuildCompactLayout([merge, mainline, shared, root], [], new HashSet<string>());

        check(collapsed.Count == 4 && collapsed[0].Commit.Parents.Count == 2,
            "Компактный граф сохраняет полный список родителей merge-коммита");
        check(collapsed[0].Commit.DisplayParents?.SequenceEqual([mainline.Hash]) == true,
            "Свёрнутый merge показывает только основную ветку");
        check(collapsed[0].BottomLaneCount == 1 && collapsed.All(row => row.BottomLaneCount <= 1),
            "Свёрнутый граф не создаёт дорожку для скрытой боковой ветки");

        var expanded = GitGraphService.BuildCompactLayout(
            [merge, mainline, shared, root],
            [side, orphan],
            new HashSet<string>([merge.Hash], StringComparer.OrdinalIgnoreCase));
        check(expanded.Count == 5 && expanded.Any(row => row.Commit.Hash == side.Hash),
            "Раскрытие merge добавляет историю второй родительской ветки");
        check(expanded[0].Commit.DisplayParents?.SequenceEqual([mainline.Hash, side.Hash]) == true,
            "Раскрытый merge соединяется с обеими видимыми родительскими ветками");
        check(expanded.All(row => (row.Commit.DisplayParents ?? []).All(parent => expanded.Any(item => item.Commit.Hash == parent))),
            "Скрытые родители не создают фиктивные дорожки");
        check(expanded.All(row => row.Commit.Hash != orphan.Hash),
            "Недостижимая боковая история не появляется в раскрытом графе");
        check(expanded.All(row => row.Lines.All(line => line.FromLane >= 0 && line.ToLane >= 0)),
            "Раскрытые линии остаются в пределах дорожек");

        var limitedExpanded = GitGraphService.BuildCompactLayout(
            [merge, mainline],
            [],
            new HashSet<string>([merge.Hash], StringComparer.OrdinalIgnoreCase));
        var limitedMerge = limitedExpanded.Single(row => row.Commit.Hash == merge.Hash);
        check(limitedMerge.HasContinuation && !limitedMerge.HasMoreMainline,
            "Обрезанная боковая ветка не помечается как продолжение основной истории");

        var truncated = GitGraphService.BuildCompactLayout(
            [merge],
            [],
            new HashSet<string>([merge.Hash], StringComparer.OrdinalIgnoreCase));
        check(truncated.Count == 1 && truncated[0].HasContinuation && truncated[0].HasMoreMainline
            && truncated[0].BottomLaneCount == 0,
            "Граница основной истории отмечена без фиктивных дорожек");
    }

    public static async Task RunIntegrationAsync(string source, Action<bool, string> check)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var service = new GitGraphService(new GitService());
        var tooManyExpansions = Enumerable.Range(1, GitGraphService.MaximumExpandedMerges + 1)
            .Select(value => value.ToString("x").PadLeft(40, '0'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejectedTooManyExpansions = false;
        try
        {
            await service.GetCompactAsync(source, 1, tooManyExpansions);
        }
        catch (GitException)
        {
            rejectedTooManyExpansions = true;
        }

        check(rejectedTooManyExpansions, "API отклоняет запрос раскрыть более восьми merge-коммитов");

        var mainlineOne = await service.GetCompactAsync(source, 1);
        var mainlineTwo = await service.GetCompactAsync(source, 2);
        check(mainlineOne.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .IsSubsetOf(mainlineTwo.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            "Увеличение лимита с одного до двух сохраняет уже видимые коммиты");

        var collapsed = await service.GetCompactAsync(source, 200);
        var moreMainline = await service.GetCompactAsync(source, 400);
        check(collapsed.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .IsSubsetOf(moreMainline.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            "Увеличение лимита основной истории с 200 до 400 сохраняет уже видимые коммиты");
        check(collapsed.All(row => (row.Commit.DisplayParents ?? row.Commit.Parents).Count <= 1
            && row.BottomLaneCount <= 1), "Компактная история показывает только основную линию");

        var mergeCandidates = collapsed.Where(row => row.Commit.Parents.Count > 1).Take(5).ToArray();
        foreach (var candidate in mergeCandidates)
        {
            var expanded = await service.GetCompactAsync(
                source,
                200,
                new HashSet<string>([candidate.Commit.Hash], StringComparer.OrdinalIgnoreCase));
            if (expanded.Count <= collapsed.Count)
            {
                continue;
            }

            var expandedMoreMainline = await service.GetCompactAsync(
                source,
                400,
                new HashSet<string>([candidate.Commit.Hash], StringComparer.OrdinalIgnoreCase));
            check(expanded.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .IsSubsetOf(expandedMoreMainline.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                "Увеличение лимита сохраняет раскрытые боковые коммиты");
            check(collapsed.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .IsSubsetOf(expanded.Select(row => row.Commit.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                "Раскрытие merge сохраняет уже видимые коммиты основной истории");

            check(expanded.Any(row => string.Equals(row.Commit.Hash, candidate.Commit.Hash, StringComparison.OrdinalIgnoreCase)
                && row.Commit.Parents.SequenceEqual(candidate.Commit.Parents)),
                "Раскрытие боковой истории сохраняет родителей исходного merge-коммита");
            var recollapsed = await service.GetCompactAsync(source, 200);
            check(recollapsed.Count == collapsed.Count, "Сворачивание возвращает компактную историю без боковых коммитов");
            return;
        }

        check(mergeCandidates.Length == 0, "В текущей истории нет merge-коммитов для интеграционной проверки раскрытия");
    }

    private static GitGraphCommit Commit(string id, IReadOnlyList<string> parents) =>
        new(id.PadRight(40, '0'), id, id, "Автор", "2026-11-15T12:00:00+07:00",
            parents.Select(parent => parent.PadRight(40, '0')).ToArray(), [], false);
}
