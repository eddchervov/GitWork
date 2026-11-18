using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class UntrackedDirectoryUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-untracked-directory-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "Online");
        var nestedWorktree = Path.Combine(repository, ".worktrees", "task");
        Directory.CreateDirectory(repository);
        var git = new GitService();
        Window? window = null;
        try
        {
            Task.Run(async () =>
            {
                await git.RunAsync(repository, "init", "--initial-branch=main");
                await git.RunAsync(repository, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(repository, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "original\n");
                await git.RunAsync(repository, "add", "--all");
                await git.RunAsync(repository, "commit", "-m", "base");
                await git.RunAsync(repository, "worktree", "add", "-b", "task", nestedWorktree);
                await File.WriteAllTextAsync(Path.Combine(nestedWorktree, "task.txt"), "nested worktree data\n");
                await File.WriteAllTextAsync(Path.Combine(repository, "ordinary.txt"), "ordinary untracked file\n");
            }).GetAwaiter().GetResult();

            var files = git.GetStatusAsync(repository, true).GetAwaiter().GetResult();
            var directory = files.Single(file => file.Path == ".worktrees/task/");
            if (!directory.IsDirectory || !directory.IsGitLink || directory.FileName != "task" || directory.StatusCode != "A"
                || directory.StatusDescription != "Ссылка на коммит вложенного репозитория" || !Directory.Exists(nestedWorktree))
            {
                throw new InvalidOperationException("Вложенный worktree должен отображаться как ссылка на коммит.");
            }

            var directoryDiff = new GitChangeService(git).GetWorkingRevisionAsync(repository, directory).GetAwaiter().GetResult();
            if (directoryDiff.Message is null || !directoryDiff.Message.Contains("ссылка на коммит", StringComparison.OrdinalIgnoreCase)
                || !directoryDiff.Message.Contains("незакоммиченные файлы", StringComparison.OrdinalIgnoreCase)
                || !directoryDiff.UnifiedText.Contains("new file mode 160000", StringComparison.Ordinal)
                || !directoryDiff.UnifiedText.Contains("Subproject commit ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Предпросмотр должен показывать ссылку 160000 и объяснять содержимое коммита.");
            }

            var view = new ChangesView("Online", [new ChangeSource("Online", repository, files)], git,
                directory.Path, repository, false);
            window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                var list = (ListBox)view.FindName("FilesList");
                var diffViewer = (DiffView)view.FindName("DiffViewer");
                var directoryEntry = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.IsDirectory);
                var ordinaryEntry = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.Path == "ordinary.txt");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (((FrameworkElement)view.FindName("LoadingText")).Visibility != Visibility.Collapsed
                    && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                if (!directoryEntry.IsIncluded || !directoryEntry.CanInclude
                    || directoryEntry.File.FileName.Length == 0 || list.SelectedItem != directoryEntry
                    || view.FindName("HistoryButton") is not Button { IsEnabled: false }
                    || diffViewer.FindName("CopyDiffButton") is not Button { IsEnabled: true }
                    || ((TextBlock)view.FindName("FileContext")).Text != L10n.T(directory.StatusDescription)
                    || ((TextBox)diffViewer.FindName("MessageText")).Text != L10n.T(directoryDiff.Message)
                    || ((RichTextBox)diffViewer.FindName("CodeViewer")).Document.Blocks.Count == 0)
                {
                    throw new InvalidOperationException("Worktree должен быть выбран и показывать предпросмотр ссылки на коммит.");
                }

                list.UpdateLayout();
                var directoryRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(directoryEntry);
                var directoryCheckbox = UntrackedDirectoryUiChecks.Descendants<CheckBox>(directoryRow).Single();
                if (!directoryCheckbox.IsEnabled || directoryCheckbox.IsChecked != true)
                {
                    throw new InvalidOperationException("Галочку worktree можно включить в коммит как ссылку.");
                }

                directoryEntry.IsIncluded = false;
                if (directoryEntry.IsIncluded)
                {
                    throw new InvalidOperationException("Ссылку на коммит worktree можно исключить вручную.");
                }

                await view.RefreshAsync();
                directoryEntry = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.IsDirectory);
                if (directoryEntry.IsIncluded)
                {
                    throw new InvalidOperationException("Обновление статуса должно сохранять выбор ссылки worktree.");
                }

                ordinaryEntry = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.Path == "ordinary.txt");
                ordinaryEntry.IsIncluded = false;
                var selectAll = (CheckBox)view.FindName("SelectAllBox");
                selectAll.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                if (!directoryEntry.IsIncluded || !ordinaryEntry.IsIncluded)
                {
                    throw new InvalidOperationException("Выбор всех файлов должен включать ссылку worktree и обычный файл.");
                }

                selectAll.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                if (directoryEntry.IsIncluded || ordinaryEntry.IsIncluded)
                {
                    throw new InvalidOperationException("Снятие выбора всех файлов должно исключать ссылку и обычный файл.");
                }
            });

            var worktrees = git.GetWorktreesAsync(repository).GetAwaiter().GetResult();
            if (!File.Exists(Path.Combine(nestedWorktree, "task.txt"))
                || !worktrees.Any(worktree => GitService.PathsEqual(worktree.Path, nestedWorktree)))
            {
                throw new InvalidOperationException("Просмотр папки не должен удалять файлы или менять регистрацию вложенного worktree.");
            }

            Console.WriteLine("OK: вложенный worktree показывается как ссылка на коммит и выбирается в коммит без своих незакоммиченных файлов.");
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(repository))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await git.RemoveWorktreeAsync(repository, nestedWorktree);
                    }
                    catch (GitException)
                    {
                        // Тестовая папка удаляется целиком сразу после проверки.
                    }
                }).GetAwaiter().GetResult();
            }

            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, true);
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in UntrackedDirectoryUiChecks.Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
