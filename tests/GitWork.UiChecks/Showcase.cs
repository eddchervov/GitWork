using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class Showcase
{
    public static void Run(Application application, string mode, string root)
    {
        L10n.SetLanguage("en");
        ThemeManager.Apply(mode == "bundle" ? "Light" : "Dark");
        var git = new GitService();
        var repositoryPath = Path.Combine(root, "Atlas");
        Task.Run(async () => await Showcase.PrepareAsync(git, repositoryPath)).GetAwaiter().GetResult();
        var repository = new RepositoryInfo("Atlas", repositoryPath, "Demo");
        var trees = Task.Run(() => git.GetWorktreesAsync(repositoryPath)).GetAwaiter().GetResult();
        var settings = new AppSettings { AutoRefresh = false, MainSection = "Worktrees", Language = "en", Theme = mode == "light" ? "Light" : "Dark" };
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1440, Height = 920 };
        foreach (var name in new[] { "Window_Loaded", "InitializeUpdateChecks" })
        {
            var handler = (RoutedEventHandler)Showcase.Method(name).CreateDelegate(typeof(RoutedEventHandler), window);
            window.Loaded -= handler;
        }

        Showcase.Field("_settings").SetValue(window, settings);
        Showcase.Field("_isUpdating").SetValue(window, true);
        Showcase.Method("UpdateThemePicker").Invoke(window, null);
        ((TreeView)window.FindName("RepositoryTree")).ItemsSource = new[]
        {
            new NavigationNode
            {
                Key = "demo", Kind = "Section", Title = "Отдельные worktree", Count = 2, IsExpanded = true,
                Children = trees.Select(tree => new NavigationNode
                {
                    Key = tree.Path, Kind = "Worktree", Title = tree.BranchLabel,
                    Subtitle = tree.IsMain ? "Atlas" : "Atlas · search improvements", Repository = repository, Worktree = tree
                }).ToArray()
            }
        };
        Showcase.Field("_isUpdating").SetValue(window, false);
        window.Loaded += async (_, _) =>
        {
            await (Task)Showcase.Method("OpenWorktreeInlineAsync").Invoke(window, [repository, trees[0], mode == "history" ? "History" : "Changes"])!;
        };
        window.Show();
        application.Run();
    }

    private static async Task PrepareAsync(GitService git, string path)
    {
        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            return;
        }

        Directory.CreateDirectory(path);
        await git.RunAsync(path, "init", "--initial-branch=main");
        await git.RunAsync(path, "config", "user.name", "Alex Morgan");
        await git.RunAsync(path, "config", "user.email", "demo@example.invalid");
        var source = "namespace Atlas.Search;\n\npublic sealed class SearchService\n{\n    public IReadOnlyList<Result> Search(string query)\n    {\n        var normalizedQuery = query.Trim();\n        var results = this._index.Find(normalizedQuery);\n\n        return results.Take(10).ToArray();\n    }\n}\n";
        await File.WriteAllTextAsync(Path.Combine(path, "SearchService.cs"), source);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Atlas\n\nA small workspace for big ideas.\n");
        await File.WriteAllTextAsync(Path.Combine(path, "legacy.config"), "limit=10\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Start Atlas search workspace");
        await git.RunAsync(path, "worktree", "add", "-b", "feature/search", Path.Combine(Path.GetDirectoryName(path)!, "Atlas-search"));
        await File.WriteAllTextAsync(Path.Combine(path, "SearchOptions.cs"), "namespace Atlas.Search;\n\npublic sealed record SearchOptions(int Limit = 25);\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Add configurable search limits");
        var feature = Path.Combine(Path.GetDirectoryName(path)!, "Atlas-search");
        await File.WriteAllTextAsync(Path.Combine(feature, "README.md"), "# Atlas\n\nFind your next idea.\n");
        await git.RunAsync(feature, "add", "--all");
        await git.RunAsync(feature, "commit", "-m", "Clarify the search experience");
        await git.RunAsync(path, "merge", "--no-ff", "feature/search", "-m", "Merge search experience improvements");
        await File.WriteAllTextAsync(Path.Combine(path, "SearchService.cs"), source.Replace("var normalizedQuery = query.Trim();", "if (string.IsNullOrWhiteSpace(query))\n        {\n            return [];\n        }\n\n        var normalizedQuery = query.Trim().ToLowerInvariant();").Replace("results.Take(10)", "results.Take(25)"));
        await File.WriteAllTextAsync(Path.Combine(path, "SearchResult.cs"), "namespace Atlas.Search;\n\npublic sealed record SearchResult(string Title, string Url);\n");
        File.Delete(Path.Combine(path, "legacy.config"));
    }

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
