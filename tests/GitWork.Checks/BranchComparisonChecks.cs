using System.Text;
using GitWork.Core;

internal static class BranchComparisonChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var repository = Path.Combine(Path.GetTempPath(), "GitWork-unrelated-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        var git = new GitService();
        try
        {
            await git.RunAsync(repository, "init", "--initial-branch=base", "-q");
            await git.RunAsync(repository, "config", "user.name", "Branch Comparison Checks");
            await git.RunAsync(repository, "config", "user.email", "comparison@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repository, "version.txt"), "base version\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--all");
            await git.RunAsync(repository, "commit", "-m", "base version");
            var baseHash = (await git.RunAsync(repository, "rev-parse", "HEAD")).Trim();

            await git.RunAsync(repository, "switch", "--orphan", "independent");
            var oldFile = Path.Combine(repository, "version.txt");
            if (File.Exists(oldFile))
            {
                File.Delete(oldFile);
            }

            await File.WriteAllTextAsync(Path.Combine(repository, "version.txt"), "independent version\n", new UTF8Encoding(false));
            await git.RunAsync(repository, "add", "--all");
            await git.RunAsync(repository, "commit", "-m", "independent version");

            var service = new GitChangeService(git);
            var versions = await service.CompareBranchesAsync(repository, "base", "independent", BranchComparisonMode.TwoVersions);
            var file = versions.Files.Single(item => item.Path == "version.txt");
            var diff = await service.GetBranchRevisionAsync(repository, versions, file);
            check(versions.MergeBaseHash.Length == 0 && versions.DiffBaseHash == baseHash
                && diff.OldText == "base version" && diff.NewText == "independent version",
                "Две независимые версии сравниваются без общей точки истории");

            string? branchChangesError = null;
            try
            {
                await service.CompareBranchesAsync(repository, "base", "independent", BranchComparisonMode.BranchChanges);
            }
            catch (GitException exception)
            {
                branchChangesError = exception.Message;
            }

            check(branchChangesError?.Contains("нет общей истории", StringComparison.Ordinal) == true,
                "Сравнение изменений ветки с независимой историей объясняет отсутствие общей точки");
        }
        finally
        {
            var fullPath = Path.GetFullPath(repository);
            var tempPath = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("GitWork-unrelated-comparison-", StringComparison.Ordinal)
                && Directory.Exists(fullPath))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories).Prepend(fullPath))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(fullPath, true);
            }
        }
    }
}
