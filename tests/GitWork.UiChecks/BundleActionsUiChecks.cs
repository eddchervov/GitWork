using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class BundleActionsUiChecks
{
    public static void Run()
    {
        BundleActionsUiChecks.CheckRemovalChoice();
        var root = Path.Combine(Path.GetTempPath(), "git-work-bundle-actions-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "main");
        Directory.CreateDirectory(repository);
        var git = new GitService();
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        MainWindow? window = null;
        try
        {
            var trees = Task.Run(async () =>
            {
                await git.RunAsync(repository, "init", "--initial-branch=main");
                await git.RunAsync(repository, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(repository, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "base\n");
                await git.RunAsync(repository, "add", "--all");
                await git.RunAsync(repository, "commit", "-m", "base");
                await git.RunAsync(repository, "worktree", "add", "-b", "first", first);
                await git.RunAsync(repository, "worktree", "add", "-b", "second", second);
                await File.WriteAllTextAsync(Path.Combine(first, "file.txt"), "first working\n");

                return await git.GetWorktreesAsync(repository);
            }).GetAwaiter().GetResult();
            var bundle = new ProjectWorkspace { Name = "Fixture", RepositoryPaths = [repository] };
            window = new MainWindow();
            typeof(MainWindow).GetField("_activeWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, bundle);
            var rows = new[]
            {
                new WorkspaceRepositoryRow { Name = "First", Path = first, RepositoryPath = repository, Worktree = trees.Single(tree => GitService.PathsEqual(tree.Path, first)) },
                new WorkspaceRepositoryRow { Name = "Second", Path = second, RepositoryPath = repository, Worktree = trees.Single(tree => GitService.PathsEqual(tree.Path, second)) }
            };
            var overview = (FrameworkElement)window.FindName("BundleOverview");
            overview.Visibility = Visibility.Visible;
            ((FrameworkElement)window.FindName("RepositoryContent")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorktreeDetailBorder")).Visibility = Visibility.Collapsed;
            ((ItemsControl)window.FindName("BundleRepositoryRows")).ItemsSource = rows;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1400, 850));
            content.Arrange(new Rect(0, 0, 1400, 850));
            content.UpdateLayout();
            var actions = BundleActionsUiChecks.Descendants<Button>(overview).Where(button => Equals(button.Content, "Действия")).ToArray();
            if (actions.Length != 2 || BundleActionsUiChecks.Descendants<Button>(overview).Any(button => button.Content is string text
                && text is "Граф Git…" or "Открыть окружение…" or "Новая ветка…" or "IDE"))
            {
                throw new InvalidOperationException("Сводка должна показывать действия отдельных репозиториев без повторяющихся общих кнопок.");
            }

            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                foreach (var row in rows)
                {
                    actions.Single(button => ReferenceEquals(button.DataContext, row)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (((FrameworkElement)window.FindName("BusyProgress")).Visibility == Visibility.Visible && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(25);
                    }

                    content.UpdateLayout();
                    var panel = (FrameworkElement)window.FindName("WorktreeDetailBorder");
                    if (overview.Visibility != Visibility.Visible || panel.Visibility != Visibility.Visible
                        || ((TextBox)window.FindName("ActionTargetPath")).Text != row.Path
                        || ((ComboBox)window.FindName("BranchPicker")).SelectedItem is not BranchInfo branch || branch.Name != row.Worktree!.Branch
                        || overview.TranslatePoint(new Point(overview.ActualWidth, 0), content).X > panel.TranslatePoint(new Point(), content).X)
                    {
                        throw new InvalidOperationException("Действия должны менять точный репозиторий, сохраняя сводку комплекта рядом с панелью.");
                    }
                }
            });
            Console.WriteLine("OK: действия репозиториев переключают целевую копию и не перекрывают сводку комплекта.");
        }
        finally
        {
            window?.Close();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, true);
        }
    }

    private static void CheckRemovalChoice()
    {
        var dirty = new WorkspaceRepositoryRemovalPreview("fixture", @"C:\fixture", @"C:\source", @"C:\fixture\copy", true, 2, 0, true, null);
        var dialog = new RemoveWorkspaceRepositoryWindow("Fixture", dirty, true);
        if (((RadioButton)dialog.FindName("KeepCopyOption")).IsChecked != true
            || !((RadioButton)dialog.FindName("DeleteCopyOption")).IsEnabled
            || !((TextBlock)dialog.FindName("SafetyWarning")).Text.Contains("2", StringComparison.Ordinal)
            || !((TextBlock)dialog.FindName("SafetyWarning")).Text.Contains("безвозвратно", StringComparison.Ordinal)
            || !((TextBlock)dialog.FindName("SafetyWarning")).Text.Contains("последний", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Удаление должно по умолчанию сохранять папку и предупреждать об изменениях и последнем репозитории.");
        }

        dialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        if (dialog.ShowDialog() != true || dialog.DeleteWorkingCopy)
        {
            throw new InvalidOperationException("Подтверждение безопасного режима не должно выбирать физическое удаление.");
        }

        var clean = dirty with { ChangedFileCount = 0, IgnoredFileCount = 10 };
        var delete = new RemoveWorkspaceRepositoryWindow("Fixture", clean, false);
        ((RadioButton)delete.FindName("DeleteCopyOption")).IsChecked = true;
        if (!delete.DeleteWorkingCopy || !Equals(((Button)delete.FindName("ConfirmButton")).Content, "Удалить с диска")
            || ((TextBlock)delete.FindName("SafetyWarning")).Text.Length != 0)
        {
            throw new InvalidOperationException("Удаление с диска должно требовать отдельного явного выбора и подписи подтверждения.");
        }

        delete.Close();
        var main = dirty with { CanDeleteWorkingCopy = false, DeleteBlockedReason = "Это основная рабочая копия." };
        var blocked = new RemoveWorkspaceRepositoryWindow("Main", main, false);
        if (((RadioButton)blocked.FindName("DeleteCopyOption")).IsEnabled)
        {
            throw new InvalidOperationException("Основную рабочую копию нельзя предлагать удалить с диска.");
        }

        blocked.Close();
        Console.WriteLine("OK: незакоммиченные изменения требуют предупреждения, ignored-файлы не мешают удалению, основная копия защищена.");
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

            foreach (var descendant in BundleActionsUiChecks.Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
