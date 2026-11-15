using GitWork.Core;

public static class HistoryChecks
{
    public static async Task RunAsync(string source, Action<bool, string> check)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "GitWork-history-checks-" + Guid.NewGuid().ToString("N"));
        var clonePath = Path.Combine(temporaryRoot, "NotesTree");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var git = new GitService();
            await git.RunAsync(temporaryRoot, "clone", "--shared", "--", Path.GetFullPath(source), clonePath);
            var paths = (await git.RunAsync(clonePath, "ls-files", "-z"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var history = new FileHistoryService(git);
            string? selectedPath = null;
            IReadOnlyList<FileHistoryEntry> selectedEntries = [];

            foreach (var path in paths)
            {
                var entries = await history.GetHistoryAsync(clonePath, path, pageSize: 50);
                if (entries.Count < 2)
                {
                    continue;
                }

                selectedPath = path;
                selectedEntries = entries;
                break;
            }

            check(selectedPath is not null, "В NotesTree найдётся файл с несколькими версиями");
            if (selectedPath is null)
            {
                return;
            }

            check(selectedEntries[0].FullHash.Length is 40 or 64, "История выдаёт полный проверяемый хеш");
            var firstPage = await history.GetHistoryAsync(clonePath, selectedPath, pageSize: 1);
            var secondPage = await history.GetHistoryAsync(clonePath, selectedPath, skip: 1, pageSize: 1);
            check(firstPage.Count == 1 && secondPage.Count == 1 && firstPage[0].FullHash != secondPage[0].FullHash,
                "История выдаёт отдельные страницы без повторения версии");

            var currentRevision = selectedEntries.First(entry => !entry.IsDeleted);
            var snapshot = await history.GetSnapshotAsync(clonePath, currentRevision);
            check(!snapshot.IsBinary && snapshot.Text.Length > 0, "Содержимое версии читается без изменения репозитория");
            var commitDiff = await history.GetCommitDiffAsync(clonePath, currentRevision);
            check(commitDiff.UnifiedText.Length > 0 || !string.IsNullOrWhiteSpace(commitDiff.Message), "Diff версии объясняет изменения или отсутствие текстового diff");
            var workingDiff = await history.GetWorkingDiffAsync(clonePath, selectedPath, currentRevision);
            check(workingDiff.UnifiedText.Length == 0 || workingDiff.Lines.Count > 0 || !string.IsNullOrWhiteSpace(workingDiff.Message),
                "Сравнение с рабочим файлом остаётся ограниченным и понятным");

            var renamedPath = selectedPath + ".history-check";
            await git.RunAsync(clonePath, "mv", "--", selectedPath, renamedPath);
            var renamedFullPath = Path.Combine(clonePath, renamedPath.Replace('/', Path.DirectorySeparatorChar));
            const string renameMarker = "History working rename check";
            await File.AppendAllTextAsync(renamedFullPath, Environment.NewLine + renameMarker);
            var renamedDiff = await history.GetWorkingDiffAsync(clonePath, selectedPath, currentRevision, renamedPath);
            check(!File.Exists(Path.Combine(clonePath, selectedPath.Replace('/', Path.DirectorySeparatorChar)))
                && renamedDiff.UnifiedText.Contains(renameMarker, StringComparison.Ordinal)
                && renamedDiff.Lines.Any(line => line.Kind == DiffLineKind.Added),
                "Diff истории показывает staged rename и изменения по новому пути");

            var rootRevision = selectedEntries.LastOrDefault(entry => entry.FirstParentHash is null);
            if (rootRevision is not null)
            {
                var rootDiff = await history.GetCommitDiffAsync(clonePath, rootRevision);
                check(rootDiff.Lines.Count > 0 || rootDiff.IsBinary || !string.IsNullOrWhiteSpace(rootDiff.Message),
                    "Первый коммит файла показывает добавленное содержимое");
            }

            var rename = selectedEntries.FirstOrDefault(entry => entry.PreviousPath is not null);
            if (rename is not null)
            {
                var renameSnapshot = await history.GetSnapshotAsync(clonePath, rename);
                check(renameSnapshot.IsBinary || renameSnapshot.Text.Length > 0 || !string.IsNullOrWhiteSpace(renameSnapshot.Message),
                    "Версия после переименования читается по историческому пути");
            }
        }
        finally
        {
            var resolvedRoot = Path.GetFullPath(temporaryRoot);
            if (resolvedRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot).StartsWith("GitWork-history-checks-", StringComparison.Ordinal))
            {
                foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(resolvedRoot, true);
            }
        }
    }
}
