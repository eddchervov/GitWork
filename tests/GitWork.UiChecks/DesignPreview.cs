using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class DesignPreview
{
    private const string FixturePrefix = "git-work-design-preview-";

    public static void Run(Application application, string outputDirectory)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), DesignPreview.FixturePrefix + Guid.NewGuid().ToString("N"));
        var screenshots = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(screenshots);

        try
        {
            var repositories = Task.Run(() => DesignPreview.PrepareAsync(fixtureRoot)).GetAwaiter().GetResult();
            var settings = new AppSettings
            {
                GitRepositoryPaths = repositories.Select(repository => repository.Path).ToList(),
                SelectedRepository = repositories.Single(repository => repository.Name == "lio-app").Path,
                AutoRefresh = false,
                MainSection = "Repositories",
                Theme = "Dark",
                Language = "ru"
            };
            L10n.SetLanguage(settings.Language);
            ThemeManager.Apply(settings.Theme);
            var window = new MainWindow { WindowState = WindowState.Normal, Width = 1600, Height = 1000 };
            foreach (var name in new[] { "Window_Loaded", "InitializeUpdateChecks" })
            {
                var handler = (RoutedEventHandler)DesignPreview.Method(name).CreateDelegate(typeof(RoutedEventHandler), window);
                window.Loaded -= handler;
            }

            DesignPreview.Field("_settings").SetValue(window, settings);
            DesignPreview.Field("_isUpdating").SetValue(window, true);
            DesignPreview.Method("UpdateThemePicker").Invoke(window, null);
            DesignPreview.Field("_isUpdating").SetValue(window, false);
            window.Show();

            try
            {
                InlineWorktreeUiChecks.RunAsync(async () =>
                {
                    await (Task)DesignPreview.Method("UpdateGitClientAsync").Invoke(window, [false])!;
                    var client = (GitClientView)((ContentControl)window.FindName("GitClientHost")).Content;
                    var repositoryPath = settings.SelectedRepository!;
                    var displayPath = "G:/work/Lio/lio-app";
                    ((TextBox)client.FindName("RepositoryLocation")).Text = displayPath;
                    ((TextBox)window.FindName("ContextStatusText")).Text = "codex/release-1.21.65  |  " + displayPath;
                    window.UpdateLayout();
                    await DesignPreview.WaitUntilAsync(() => client.CanLeave
                        && client.FindName("ViewHost") is ContentControl { Content: ChangesView changes } && changes.CanLeave);
                    var repositoryList = (ListBox)client.FindName("RepositoriesList");
                    await DesignPreview.WaitUntilAsync(() => repositoryList.ItemContainerGenerator.ContainerFromItem(repositoryList.SelectedItem) is ListBoxItem
                    {
                        Tag: RepositoryRowState { ChangeCount: 4, HasChanges: true }
                    });
                    var changesView = (ChangesView)((ContentControl)client.FindName("ViewHost")).Content;
                    var changedFiles = (ListBox)changesView.FindName("FilesList");
                    changedFiles.SelectedItem = changedFiles.Items.OfType<ChangeFileEntry>()
                        .Single(entry => entry.File.Path == "src/Lio.Api/RequestLoggingMiddleware.cs");
                    await DesignPreview.WaitUntilAsync(() => ((FrameworkElement)changesView.FindName("LoadingText")).Visibility == Visibility.Collapsed);
                    DesignPreview.Capture(window, Path.Combine(screenshots, "changes.png"));
                    var diffViewer = (DiffView)changesView.FindName("DiffViewer");
                    diffViewer.IsUnified = false;
                    DesignPreview.Capture(window, Path.Combine(screenshots, "changes-split.png"));
                    diffViewer.IsUnified = true;
                    changedFiles.SelectedItem = changedFiles.Items.OfType<ChangeFileEntry>()
                        .Single(entry => entry.File.Path == "tests/Lio.Api.Tests/RequestLoggingMiddlewareTests.cs");
                    await DesignPreview.WaitUntilAsync(() => ((FrameworkElement)changesView.FindName("LoadingText")).Visibility == Visibility.Collapsed);
                    DesignPreview.Capture(window, Path.Combine(screenshots, "syntax-added.png"));

                    var historyTab = (RadioButton)client.FindName("HistoryTab");
                    historyTab.IsChecked = true;
                    var history = await DesignPreview.WaitForHistoryAsync(client);
                    var desiredCommit = ((ListBox)history.FindName("CommitsList")).Items.OfType<GraphCommitItem>()
                        .FirstOrDefault(item => item.Row.Commit.Subject.Contains("логирование", StringComparison.OrdinalIgnoreCase))
                        ?? ((ListBox)history.FindName("CommitsList")).Items.OfType<GraphCommitItem>().Skip(1).FirstOrDefault();
                    if (desiredCommit is not null)
                    {
                        var commits = (ListBox)history.FindName("CommitsList");
                        commits.SelectedItem = desiredCommit;
                        commits.ScrollIntoView(desiredCommit);
                        await DesignPreview.WaitUntilAsync(() => Equals(commits.SelectedItem, desiredCommit)
                            && ((ListBox)history.FindName("CommitFiles")).Items.Count > 0);
                    }

                    await DesignPreview.WaitForDiffAsync(history);
                    ((TextBox)client.FindName("RepositoryLocation")).Text = displayPath;
                    ((TextBox)window.FindName("ContextStatusText")).Text = "codex/release-1.21.65  |  " + displayPath;
                    DesignPreview.Capture(window, Path.Combine(screenshots, "history.png"));

                    var git = new GitService();
                    await git.RunAsync(repositoryPath, "restore", "--staged", "--worktree", "--", "README.md", "src/Lio.Api/RequestLoggingMiddleware.cs");
                    File.Delete(Path.Combine(repositoryPath, "src", "Lio.Api", "appsettings.Development.json"));
                    File.Delete(Path.Combine(repositoryPath, "tests", "Lio.Api.Tests", "RequestLoggingMiddlewareTests.cs"));
                    var remainingChanges = await git.GetStatusAsync(repositoryPath, true);
                    if (remainingChanges.Count != 0)
                    {
                        throw new InvalidOperationException("Не удалось очистить демонстрационные изменения перед снимком выбора ветки.");
                    }

                    await client.RefreshAsync();
                    await DesignPreview.WaitUntilAsync(() => client.CanLeave && client.FindName("ViewHost") is ContentControl { Content: GitHistoryView view } && !view.IsBusy);
                    await DesignPreview.WaitUntilAsync(() => ((RadioButton)client.FindName("ChangesTab")).Content?.ToString() == L10n.T("Изменения"));
                    ((Button)client.FindName("BranchSelectorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await DesignPreview.WaitUntilAsync(() => ((FrameworkElement)client.FindName("BranchPickerHost")).Visibility == Visibility.Visible);
                    ((TextBox)client.FindName("RepositoryLocation")).Text = displayPath;
                    ((TextBox)window.FindName("ContextStatusText")).Text = "codex/release-1.21.65  |  " + displayPath;
                    DesignPreview.Capture(window, Path.Combine(screenshots, "branches.png"));
                });
            }
            finally
            {
                window.Close();
            }

            application.Shutdown();
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                DesignPreview.DeleteFixture(fixtureRoot);
            }
        }
    }

    private static async Task<IReadOnlyList<RepositoryInfo>> PrepareAsync(string fixtureRoot)
    {
        var git = new GitService();
        var repositories = new List<RepositoryInfo>();
        (string Group, string Name)[] demoRepositories =
        [
            ("ED", "Git Work"), ("ED", "NotesTree"),
            ("Lio", "lio"), ("Lio", "lio-app"),
            ("TechnoKad", "Online"), ("TechnoKad", "SharedKernel"),
            ("TechnoKad", "SharedWebTemplates"), ("TechnoKad", "Tfs")
        ];

        foreach (var (group, name) in demoRepositories)
        {
            var path = Path.Combine(fixtureRoot, group, name);
            repositories.Add(new RepositoryInfo(name, path, group));
            if (name == "lio-app")
            {
                await DesignPreview.PrepareMainRepositoryAsync(git, path);
            }
            else
            {
                await DesignPreview.PrepareSmallRepositoryAsync(git, path, name);
            }
        }

        return repositories;
    }

    private static async Task PrepareMainRepositoryAsync(GitService git, string path)
    {
        Directory.CreateDirectory(path);
        await git.RunAsync(path, "init", "--initial-branch=master");
        await DesignPreview.ConfigureRepositoryAsync(git, path);
        Directory.CreateDirectory(Path.Combine(path, "src", "Lio.Api"));
        Directory.CreateDirectory(Path.Combine(path, "tests", "Lio.Api.Tests"));
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Lio API\n\nСервис управления API-запросами.\n");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "RequestLoggingMiddleware.cs"), DesignPreview.InitialMiddleware);
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "ApiOptions.cs"), "namespace Lio.Api;\n\npublic sealed record ApiOptions(int RequestTimeoutSeconds = 30);\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Создать основу API");

        await git.RunAsync(path, "switch", "-c", "feature/mobile-logs");
        await File.WriteAllTextAsync(Path.Combine(path, "tests", "Lio.Api.Tests", "ApiRequestLoggingMiddlewareTests.cs"), DesignPreview.InitialTests);
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Добавить тесты логирования API");
        await git.RunAsync(path, "switch", "master");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "ApiOptions.cs"), "namespace Lio.Api;\n\npublic sealed record ApiOptions(int RequestTimeoutSeconds = 45, bool IncludeRequestId = true);\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Настроить параметры запросов");
        await git.RunAsync(path, "merge", "--no-ff", "feature/mobile-logs", "-m", "Merge branch 'feature/mobile-logs'");

        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "RequestTiming.cs"), "namespace Lio.Api;\n\npublic sealed class RequestTiming\n{\n    public TimeSpan Elapsed { get; init; }\n    public int StatusCode { get; init; }\n}\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Добавить метрики времени ответа");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "ApiOptions.cs"), "namespace Lio.Api;\n\npublic sealed record ApiOptions(int RequestTimeoutSeconds = 45, bool IncludeRequestId = true, bool LogResponseBody = false);\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Уточнить настройки журналирования");
        await File.WriteAllTextAsync(Path.Combine(path, "tests", "Lio.Api.Tests", "ApiErrorHandlingTests.cs"), "namespace Lio.Api.Tests;\n\npublic sealed class ApiErrorHandlingTests\n{\n    public void UnavailableServiceReturnsExpectedStatus() { }\n}\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Проверить ошибки upstream");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "RequestLoggingMiddleware.cs"), DesignPreview.UpdatedMiddleware);
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Улучшить логирование запросов");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "RequestTiming.cs"), "namespace Lio.Api;\n\npublic sealed class RequestTiming\n{\n    public TimeSpan Elapsed { get; init; }\n    public int StatusCode { get; init; }\n    public string RequestId { get; init; } = string.Empty;\n}\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Добавить идентификатор запроса в метрики");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "ApiOptions.cs"), "namespace Lio.Api;\n\npublic sealed record ApiOptions(int RequestTimeoutSeconds = 60, bool IncludeRequestId = true, bool LogResponseBody = false);\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Настроить таймаут клиента");
        await git.RunAsync(path, "switch", "-c", "codex/release-1.21.65");
        await git.RunAsync(path, "branch", "develop");
        await git.RunAsync(path, "branch", "release/1.21.0");
        await git.RunAsync(path, "branch", "release/1.20.2", "HEAD~2");
        await git.RunAsync(path, "branch", "fix/login-timeout");
        await git.RunAsync(path, "branch", "hotfix/session");

        var remotePath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, "remotes", "lio-app.git");
        Directory.CreateDirectory(Path.GetDirectoryName(remotePath)!);
        Directory.CreateDirectory(remotePath);
        await git.RunAsync(remotePath, "init", "--bare", "--initial-branch=master");
        await git.RunAsync(path, "remote", "add", "origin", remotePath);
        DesignPreview.CopyDirectory(Path.Combine(path, ".git", "objects"), Path.Combine(remotePath, "objects"));
        foreach (var branch in new[] { "master", "develop", "release/1.20.2" })
        {
            var hash = (await git.RunAsync(path, "rev-parse", branch)).Trim();
            await git.RunAsync(remotePath, "update-ref", "refs/heads/" + branch, hash);
            await git.RunAsync(path, "update-ref", "refs/remotes/origin/" + branch, hash);
        }

        await git.RunAsync(path, "branch", "--set-upstream-to=origin/master", "codex/release-1.21.65");
        var linkedPath = Path.Combine(Path.GetDirectoryName(path)!, "lio-app-feature");
        await git.RunAsync(path, "worktree", "add", "--", linkedPath, "feature/mobile-logs");

        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Lio API\n\nСервис управления API-запросами.\n\nВ работе: обновление журналирования.\n");
        await git.RunAsync(path, "add", "README.md");
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "RequestLoggingMiddleware.cs"), DesignPreview.WorkingMiddleware);
        await File.WriteAllTextAsync(Path.Combine(path, "src", "Lio.Api", "appsettings.Development.json"), "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Default\": \"Debug\"\n    }\n  }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(path, "tests", "Lio.Api.Tests", "RequestLoggingMiddlewareTests.cs"), DesignPreview.AddedTests);
    }

    private static async Task PrepareSmallRepositoryAsync(GitService git, string path, string name)
    {
        Directory.CreateDirectory(path);
        await git.RunAsync(path, "init", "--initial-branch=main");
        await DesignPreview.ConfigureRepositoryAsync(git, path);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), $"# {name}\n\nЛокальный демонстрационный репозиторий.\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", "Создать репозиторий " + name);
    }

    private static async Task ConfigureRepositoryAsync(GitService git, string path)
    {
        await git.RunAsync(path, "config", "user.name", "Иван Петров");
        await git.RunAsync(path, "config", "user.email", "ivan@example.invalid");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            DesignPreview.CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static async Task<GitHistoryView> WaitForHistoryAsync(GitClientView client)
    {
        GitHistoryView? history = null;
        await DesignPreview.WaitUntilAsync(() =>
        {
            history = (client.FindName("ViewHost") as ContentControl)?.Content as GitHistoryView;
            return client.CanLeave && history is not null && !history.IsBusy
                && ((ListBox)history.FindName("CommitsList")).Items.Count >= 9;
        });

        return history!;
    }

    private static async Task WaitForDiffAsync(GitHistoryView history)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var files = (ListBox)history.FindName("CommitFiles");
            var diff = (FrameworkElement)history.FindName("CommitDiff");
            var diffView = (DiffView)diff;
            var hasDiff = diffView.Lines.Count > 0 || diffView.UnifiedLines.Count > 0
                || ((FrameworkElement)diffView.FindName("MessageBorder")).Visibility == Visibility.Visible;
            if (files.Items.Count > 0 && diff.IsLoaded && diff.ActualHeight > 100 && hasDiff)
            {
                await Task.Delay(250);

                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("Демонстрационный diff не успел загрузиться.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Окно предпросмотра не завершило загрузку за отведённое время.");
            }

            await Task.Delay(25);
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var root = (Grid)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void DeleteFixture(string fixtureRoot)
    {
        var fullPath = Path.GetFullPath(fixtureRoot);
        var directoryName = Path.GetFileName(fullPath);
        if (!directoryName.StartsWith(DesignPreview.FixturePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(directoryName[DesignPreview.FixturePrefix.Length..], "N", out _)
            || !string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Не удалось проверить путь временного предпросмотра; папка оставлена без удаления.");
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(fullPath, true);
    }

    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private const string InitialMiddleware = """
        using System.Diagnostics;
        using Microsoft.AspNetCore.Http;
        using Microsoft.Extensions.Logging;

        namespace Lio.Api;

        public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            public async Task InvokeAsync(HttpContext context)
            {
                var requestId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                using (logger.BeginScope(new Dictionary<string, object> { ["request_id"] = requestId }))
                {
                    logger.LogInformation("request_started");
                    await next(context);
                    logger.LogInformation("request_completed");
                }
            }
        }
        """;

    private const string UpdatedMiddleware = """
        using System.Diagnostics;
        using Microsoft.AspNetCore.Http;
        using Microsoft.Extensions.Logging;

        namespace Lio.Api;

        public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            public async Task InvokeAsync(HttpContext context)
            {
                var requestId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                var timer = Stopwatch.StartNew();
                using (logger.BeginScope(new Dictionary<string, object> { ["request_id"] = requestId }))
                {
                    logger.LogInformation("request_started {Method} {Path}", context.Request.Method, context.Request.Path);
                    await next(context);
                    timer.Stop();
                    logger.LogInformation("request_completed {StatusCode} {ElapsedMs}ms", context.Response.StatusCode, timer.ElapsedMilliseconds);
                }
            }
        }
        """;

    private const string WorkingMiddleware = """
        using System.Diagnostics;
        using Microsoft.AspNetCore.Http;
        using Microsoft.Extensions.Logging;

        namespace Lio.Api;

        public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            public async Task InvokeAsync(HttpContext context)
            {
                var requestId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                var timer = Stopwatch.StartNew();
                using (logger.BeginScope(new Dictionary<string, object> { ["request_id"] = requestId }))
                {
                    logger.LogInformation("request_started {Method} {Path} {RequestId}", context.Request.Method, context.Request.Path, requestId);
                    await next(context);
                    timer.Stop();
                    logger.LogInformation("request_completed {StatusCode} {ElapsedMs}ms {RequestId}", context.Response.StatusCode, timer.ElapsedMilliseconds, requestId);
                }
            }
        }
        """;

    private const string AddedTests = """
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http;
        using Microsoft.Extensions.Logging;
        using Xunit;

        namespace Lio.Api.Tests;

        public sealed class RequestLoggingMiddlewareTests
        {
            [Fact]
            public async Task RequestIdIsIncludedInLogScope()
            {
                // Для каждого запроса в журнале сохраняется идентификатор.
                var context = new DefaultHttpContext();
                context.Request.Method = "GET";
                context.Request.Path = "/api/health";
                context.TraceIdentifier = "request-42";

                var logger = new TestLogger<RequestLoggingMiddleware>();
                var middleware = new RequestLoggingMiddleware(
                    _ => Task.CompletedTask,
                    logger);

                await middleware.InvokeAsync(context);

                Assert.Equal(200, context.Response.StatusCode);
                Assert.Contains("request-42", logger.Messages);
            }
        }
        """;

    private const string InitialTests = """
        namespace Lio.Api.Tests;

        public sealed class ApiRequestLoggingMiddlewareTests
        {
            public void RequestStartAndCompletionAreLogged() { }
        }
        """;
}
