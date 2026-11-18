using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class FileMenusUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-file-menus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        Window? window = null;
        ChangesView? changes = null;
        GitHistoryView? history = null;
        FileHistoryWindow? fileHistory = null;
        try
        {
            Task.Run(async () =>
            {
                await git.RunAsync(root, "init", "--initial-branch=main");
                await git.RunAsync(root, "config", "user.name", "Git Work UI checks");
                await git.RunAsync(root, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(root, "tracked.cs"), "public class Sample\n{\n    // Комментарий.\n    public string Name { get; set; } = \"name\";\n}\n");
                await git.RunAsync(root, "add", "--all");
                await git.RunAsync(root, "commit", "-m", "base");
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                await File.WriteAllTextAsync(Path.Combine(root, "logs", "new.txt"), "new\n");
                await File.WriteAllTextAsync(Path.Combine(root, "tracked.cs"), "public class Changed { }\n");
            }).GetAwaiter().GetResult();

            var files = Task.Run(() => git.GetStatusAsync(root, true)).GetAwaiter().GetResult();
            changes = new ChangesView("Fixture", [new ChangeSource("Fixture", root, files)], git, autoRefresh: false);
            window = new Window { Content = changes, Width = 1000, Height = 700, ShowInTaskbar = false };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                await FileMenusUiChecks.WaitUntilAsync(() => !changes.IsBusy);
                var list = (ListBox)changes.FindName("FilesList");
                var discardAll = (Button)changes.FindName("DiscardAllButton");
                ((TextBox)changes.FindName("SearchBox")).Text = "nothing-matches";
                if (!discardAll.IsEnabled)
                {
                    throw new InvalidOperationException("Общая отмена должна учитывать файлы, скрытые поиском.");
                }

                ((TextBox)changes.FindName("SearchBox")).Clear();
                var tracked = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.Path == "tracked.cs");
                var menu = FileMenusUiChecks.OpenMenu(window, list, tracked);
                foreach (var action in new[] { "open", "ide", "reveal", "history", "copy-full", "copy-relative", "ignore-file", "ignore-folder", "toggle-included", "discard" })
                {
                    _ = FileMenusUiChecks.Action(menu, action);
                }

                var included = tracked.IsIncluded;
                var toggle = FileMenusUiChecks.Action(menu, "toggle-included");
                menu.IsOpen = false;
                toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (tracked.IsIncluded == included)
                {
                    throw new InvalidOperationException("Меню должно менять включение выбранного файла в коммит.");
                }

                var untracked = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.Path == "logs/new.txt");
                menu = FileMenusUiChecks.OpenMenu(window, list, untracked);
                var ignore = FileMenusUiChecks.Action(menu, "ignore-file");
                menu.IsOpen = false;
                ignore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await FileMenusUiChecks.WaitUntilAsync(() => !changes.IsBusy
                    && !list.Items.Cast<ChangeFileEntry>().Any(entry => entry.File.Path == "logs/new.txt"));
                if (!File.ReadAllText(Path.Combine(root, ".gitignore")).Contains("/logs/new.txt", StringComparison.Ordinal)
                    || !File.Exists(Path.Combine(root, "logs", "new.txt")))
                {
                    throw new InvalidOperationException("Игнорирование должно добавить точное правило и обновить список без удаления файла.");
                }

                changes.Detach();
                history = new GitHistoryView([new GraphRepositoryChoice("Fixture", root)], git);
                window.Content = history;
                await history.RefreshAsync();
                var historyFiles = (ListBox)history.FindName("CommitFiles");
                await FileMenusUiChecks.WaitUntilAsync(() => !history.IsBusy && historyFiles.Items.Count > 0);
                menu = FileMenusUiChecks.OpenMenu(window, historyFiles, historyFiles.Items[0]);
                if (menu.Items.OfType<MenuItem>().Any(item => item.Tag is "discard" or "ignore-file" or "toggle-included")
                    || !FileMenusUiChecks.Action(menu, "copy-relative").IsEnabled)
                {
                    throw new InvalidOperationException("Историческое меню должно предоставлять пути и открытие без отмены текущих изменений.");
                }

                menu.IsOpen = false;
                history.Detach();
                fileHistory = new FileHistoryWindow(root, "tracked.cs", git) { Owner = window };
                fileHistory.Show();
                var snapshot = (RichTextBox)fileHistory.FindName("SnapshotText");
                await FileMenusUiChecks.WaitUntilAsync(() => snapshot.Document.Blocks.OfType<Paragraph>()
                    .SelectMany(paragraph => paragraph.Inlines.OfType<Run>()).Any(run => run.Text == "Sample"));
                var runs = snapshot.Document.Blocks.OfType<Paragraph>()
                    .SelectMany(paragraph => paragraph.Inlines.OfType<Run>()).ToArray();
                if (!runs.Any(run => run.Text == "public" && run.Foreground == fileHistory.FindResource("SyntaxKeywordBrush"))
                    || !runs.Any(run => run.Text == "Sample" && run.Foreground == fileHistory.FindResource("SyntaxTypeBrush"))
                    || !runs.Any(run => run.Text.Contains("Комментарий", StringComparison.Ordinal)
                        && run.Foreground == fileHistory.FindResource("SyntaxCommentBrush"))
                    || !new TextRange(snapshot.Document.ContentStart, snapshot.Document.ContentEnd).Text.Contains("public string Name { get; set; } = \"name\";", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Полная версия файла должна показывать тот же синтаксис, что diff, сохраняя копируемый код.");
                }

                fileHistory.Close();
            });
        }
        finally
        {
            changes?.Detach();
            history?.Detach();
            fileHistory?.Close();
            window?.Close();
            var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("GitWork-file-menus-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Небезопасный путь временной проверки.");
            }

            if (Directory.Exists(root))
            {
                InlineWorktreeUiChecks.RunAsync(async () =>
                {
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(root))
                            {
                                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                                {
                                    File.SetAttributes(file, FileAttributes.Normal);
                                }

                                Directory.Delete(root, true);
                            }

                            return;
                        }
                        catch (IOException) when (attempt < 39)
                        {
                            await Task.Delay(50);
                        }
                    }
                });
            }
        }

        Console.WriteLine("OK: меню файлов, общая отмена при поиске и синтаксис полной версии в истории файла.");
    }

    private static ContextMenu OpenMenu(Window window, ListBox list, object entry)
    {
        window.UpdateLayout();
        list.ScrollIntoView(entry);
        window.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(entry);
        foreach (var routedEvent in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
        {
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = routedEvent });
        }

        if (!Equals(list.SelectedItem, entry) || list.ContextMenu is not { IsOpen: true } menu)
        {
            throw new InvalidOperationException("Первый правый клик должен выбрать нужный файл и открыть меню.");
        }

        return menu;
    }

    private static MenuItem Action(ContextMenu menu, string tag) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, tag));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("Истекло время ожидания файлового меню.");
            }

            await Task.Delay(30);
        }
    }
}
