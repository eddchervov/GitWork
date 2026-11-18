using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class TabNavigationUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-tab-navigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        Window? window = null;
        try
        {
            Task.Run(async () =>
            {
                await git.RunAsync(root, "init", "--initial-branch=main");
                await git.RunAsync(root, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(root, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "base\n");
                await git.RunAsync(root, "add", "--all");
                await git.RunAsync(root, "commit", "-m", "base");
            }).GetAwaiter().GetResult();

            var client = new GitClientView(git, new AppSettings { AutoRefresh = false });
            window = new Window { Content = client, Width = 1000, Height = 700 };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                await client.SetRepositoriesAsync([new RepositoryInfo("Fixture", root, "Tests")]);
                var changesTab = (RadioButton)client.FindName("ChangesTab");
                var historyTab = (RadioButton)client.FindName("HistoryTab");
                var comparisonTab = (RadioButton)client.FindName("ComparisonTab");
                var host = (ContentControl)client.FindName("ViewHost");

                historyTab.IsChecked = true;
                var loadingHistory = (GitHistoryView)host.Content;
                var historyBusyField = typeof(GitHistoryView).GetField("_isBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
                historyBusyField.SetValue(loadingHistory, true);
                comparisonTab.IsChecked = true;
                await client.Dispatcher.InvokeAsync(() => { });
                TabNavigationUiChecks.AssertSelected(historyTab, changesTab, comparisonTab);
                if (host.Content != loadingHistory)
                {
                    throw new InvalidOperationException("Операция записи должна блокировать переход и во время обновления заголовка.");
                }

                historyBusyField.SetValue(loadingHistory, false);
                await TabNavigationUiChecks.WaitUntilAsync(() => client.CanLeave);
                changesTab.IsChecked = true;
                await TabNavigationUiChecks.WaitUntilAsync(() => host.Content is ChangesView && client.CanLeave);

                for (var iteration = 0; iteration < 3; iteration++)
                {
                    historyTab.IsChecked = true;
                    changesTab.IsChecked = true;
                    comparisonTab.IsChecked = true;
                    changesTab.IsChecked = true;
                    historyTab.IsChecked = true;
                    await TabNavigationUiChecks.WaitUntilAsync(() => host.Content is GitHistoryView && client.CanLeave);
                    TabNavigationUiChecks.AssertSelected(historyTab, changesTab, comparisonTab);

                    var history = (GitHistoryView)host.Content;
                    await TabNavigationUiChecks.WaitUntilAsync(() => !history.IsBusy);
                    var reload = history.RefreshAsync();
                    if (!history.IsBusy || !history.CanLeave)
                    {
                        throw new InvalidOperationException("Чтение истории не должно блокировать переход на другую вкладку.");
                    }

                    comparisonTab.IsChecked = true;
                    historyTab.IsChecked = true;
                    changesTab.IsChecked = true;
                    comparisonTab.IsChecked = true;
                    await TabNavigationUiChecks.WaitUntilAsync(() => host.Content is BranchComparisonView && client.CanLeave);
                    TabNavigationUiChecks.AssertSelected(comparisonTab, changesTab, historyTab);
                    await reload;

                    changesTab.IsChecked = true;
                    historyTab.IsChecked = true;
                    comparisonTab.IsChecked = true;
                    changesTab.IsChecked = true;
                    await TabNavigationUiChecks.WaitUntilAsync(() => host.Content is ChangesView && client.CanLeave);
                    TabNavigationUiChecks.AssertSelected(changesTab, historyTab, comparisonTab);
                    await Task.Delay(100);
                    if (host.Content is not ChangesView)
                    {
                        throw new InvalidOperationException("Завершение старой загрузки не должно подменять последнюю вкладку.");
                    }
                }

                var changes = (ChangesView)host.Content;
                var busyField = typeof(ChangesView).GetField("_isBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
                busyField.SetValue(changes, true);
                historyTab.IsChecked = true;
                await client.Dispatcher.InvokeAsync(() => { });
                TabNavigationUiChecks.AssertSelected(changesTab, historyTab, comparisonTab);
                if (host.Content != changes)
                {
                    throw new InvalidOperationException("Операция записи должна блокировать смену вкладки.");
                }

                busyField.SetValue(changes, false);
                historyTab.IsChecked = true;
                await TabNavigationUiChecks.WaitUntilAsync(() => host.Content is GitHistoryView && client.CanLeave);
                TabNavigationUiChecks.AssertSelected(historyTab, changesTab, comparisonTab);
                await TabNavigationUiChecks.WaitUntilAsync(() => host.Content is GitHistoryView { IsBusy: false });
            });

            Console.WriteLine("OK: быстрые переключения сохраняют последнюю вкладку и не блокируются чтением истории.");
        }
        finally
        {
            window?.Close();
            var fullRoot = Path.GetFullPath(root);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("git-work-tab-navigation-", StringComparison.Ordinal)
                && Directory.Exists(fullRoot))
            {
                foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(fullRoot, true);
            }
        }
    }

    private static void AssertSelected(RadioButton selected, RadioButton other, RadioButton another)
    {
        if (selected.IsChecked != true || other.IsChecked == true || another.IsChecked == true)
        {
            throw new InvalidOperationException("Выделение должно соответствовать последней открытой вкладке.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Последняя выбранная вкладка не загрузилась.");
            }

            await Task.Delay(10);
        }
    }
}
