using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class InlineWorktreeUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-inline-ui-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "main");
        var linked = Path.Combine(root, "linked");
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
                await File.WriteAllTextAsync(Path.Combine(repository, "shared.txt"), "base\n");
                await File.WriteAllTextAsync(Path.Combine(repository, "main-only.txt"), "base\n");
                await git.RunAsync(repository, "add", "--all");
                await git.RunAsync(repository, "commit", "-m", "base");
                await git.RunAsync(repository, "worktree", "add", "-b", "task", linked);
                await File.WriteAllTextAsync(Path.Combine(linked, "shared.txt"), "task commit\n");
                await git.RunAsync(linked, "add", "--all");
                await git.RunAsync(linked, "commit", "-m", "task");
                await File.WriteAllTextAsync(Path.Combine(repository, "remote.txt"), "remote commit\n");
                await git.RunAsync(repository, "add", "--all");
                await git.RunAsync(repository, "commit", "-m", "remote");
                var remoteHash = (await git.RunAsync(repository, "rev-parse", "HEAD")).Trim();
                await git.RunAsync(repository, "remote", "add", "origin", repository);
                await git.RunAsync(repository, "update-ref", "refs/remotes/origin/main", remoteHash);
                await git.RunAsync(repository, "branch", "--set-upstream-to=origin/main", "task");
                await File.WriteAllTextAsync(Path.Combine(repository, "main-only.txt"), "main working\n");
                await File.WriteAllTextAsync(Path.Combine(linked, "shared.txt"), "task working\n");
                await File.WriteAllTextAsync(Path.Combine(linked, "linked-only.txt"), "linked working\n");
            }).GetAwaiter().GetResult();

            var settings = new AppSettings { GitRepositoryPaths = [], SelectedRepository = "unchanged", AutoRefresh = false };
            var view = new GitClientView(git, settings) { EmbeddedInWorkspace = true };
            window = new Window { Content = view, Width = 1100, Height = 750 };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                await view.OpenCopyAsync(new RepositoryInfo("Fixture", repository, ""), linked, "Changes", workspaceContext: true);
                var host = (ContentControl)view.FindName("ViewHost");
                if (host.Content is not ChangesView changes || changes.SelectedSource is not { } source
                    || !GitService.PathsEqual(source.Path, linked)
                    || ((FrameworkElement)view.FindName("RepositorySidebar")).Visibility != Visibility.Collapsed
                    || ((Button)view.FindName("BackToWorkspace")).Visibility != Visibility.Collapsed)
                {
                    throw new InvalidOperationException("Встроенный клиент должен сразу показывать изменения точного worktree без второй навигации и кнопки возврата.");
                }

                var files = ((ListBox)changes.FindName("FilesList")).Items.Cast<ChangeFileEntry>().Select(entry => entry.File.Path).ToArray();
                if (!files.Order().SequenceEqual(new[] { "linked-only.txt", "shared.txt" }))
                {
                    throw new InvalidOperationException("Изменения основной копии не должны подмешиваться в выбранный worktree.");
                }

                ((RadioButton)view.FindName("HistoryTab")).IsChecked = true;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && (host.Content is not GitHistoryView history
                    || history.IsBusy || ((TextBlock)history.FindName("OutgoingCount")).Text != "1"
                    || ((TextBlock)history.FindName("IncomingCount")).Text != "1"))
                {
                    await Task.Delay(25);
                }

                if (host.Content is not GitHistoryView loaded
                    || ((TextBlock)loaded.FindName("OutgoingCount")).Text != "1"
                    || ((TextBlock)loaded.FindName("IncomingCount")).Text != "1"
                    || ((FrameworkElement)loaded.FindName("SyncCounters")).Visibility != Visibility.Visible
                    || ((FrameworkElement)loaded.FindName("RemoteSelector")).Visibility != Visibility.Collapsed
                    || ((TextBlock)loaded.FindName("BranchSummary")).Visibility != Visibility.Collapsed)
                {
                    throw new InvalidOperationException("История worktree должна показывать выделенные счётчики 1/1 и скрывать повтор веток и единственный сервер.");
                }

                if (settings.GitRepositoryPaths?.Count != 0 || settings.SelectedRepository != "unchanged")
                {
                    throw new InvalidOperationException("Просмотр worktree не должен менять ручной список репозиториев.");
                }

                window.UpdateLayout();
                var toolbar = (FrameworkElement)loaded.FindName("Toolbar");
                var tabs = (FrameworkElement)view.FindName("ViewTabs");
                var body = (FrameworkElement)loaded.FindName("ClientBody");
                if (((ContentControl)view.FindName("ViewActionsHost")).Content != toolbar
                    || Math.Abs(toolbar.TranslatePoint(new Point(0, toolbar.ActualHeight / 2), view).Y
                        - tabs.TranslatePoint(new Point(0, tabs.ActualHeight / 2), view).Y) > 1
                    || body.TranslatePoint(new Point(), loaded).Y > 12)
                {
                    throw new InvalidOperationException("Кнопки синхронизации должны стоять рядом с вкладками без отдельной пустой полосы над историей.");
                }

                var contentWidth = host.ActualWidth;
                ((Button)view.FindName("BranchSelectorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await InlineWorktreeUiChecks.WaitUntilAsync(() => view.CanLeave);
                view.UpdateLayout();
                if (((FrameworkElement)view.FindName("BranchPickerHost")).Visibility != Visibility.Visible
                    || host.ActualWidth < contentWidth || view.FindName("ActionsPanel") is not null)
                {
                    throw new InvalidOperationException("Панель веток должна открываться поверх содержимого, сохраняя ширину diff и не создавая боковую панель действий.");
                }

                await InlineWorktreeUiChecks.CheckRepositoryNavigationAsync(window, view, git, repository, linked);
            });
            Console.WriteLine("OK: worktree сразу открывается в Changes, история показывает счётчики 1/1, ручные репозитории не меняются.");
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

    private static async Task CheckRepositoryNavigationAsync(Window window, GitClientView worktreeView, GitService git, string repository, string linked)
    {
        var repositoriesView = new GitClientView(git, new AppSettings { AutoRefresh = false });
        window.Content = null;
        var panel = new Grid();
        panel.Children.Add(worktreeView);
        panel.Children.Add(repositoriesView);
        window.Content = panel;
        var repositories = new[]
        {
            new RepositoryInfo("Main", repository, "lio"),
            new RepositoryInfo("Linked", linked, "lio")
        };

        await repositoriesView.SetRepositoriesAsync(repositories);
        var changesTab = (RadioButton)repositoriesView.FindName("ChangesTab");
        var worktreeHistory = (RadioButton)worktreeView.FindName("HistoryTab");
        if (changesTab.IsChecked != true || worktreeHistory.IsChecked != true
            || ((ContentControl)repositoriesView.FindName("ViewHost")).Content is not ChangesView)
        {
            throw new InvalidOperationException("Репозитории должны открывать изменения, сохраняя выбранную историю worktree в том же окне.");
        }

        ((RadioButton)worktreeView.FindName("ChangesTab")).IsChecked = true;
        await InlineWorktreeUiChecks.WaitUntilAsync(() => worktreeView.CanLeave);
        worktreeHistory.IsChecked = true;
        await InlineWorktreeUiChecks.WaitUntilAsync(() => worktreeView.CanLeave);
        await repositoriesView.SetRepositoriesAsync(repositories);
        if (changesTab.IsChecked != true || worktreeHistory.IsChecked != true)
        {
            throw new InvalidOperationException("Повторные переключения не должны снимать выбор вкладки другого клиента.");
        }

        ((RadioButton)repositoriesView.FindName("HistoryTab")).IsChecked = true;
        await InlineWorktreeUiChecks.WaitUntilAsync(() => repositoriesView.CanLeave);
        await repositoriesView.SetRepositoriesAsync(repositories, resetView: true);
        if (changesTab.IsChecked != true || worktreeHistory.IsChecked != true
            || ((ContentControl)repositoriesView.FindName("ViewActionsHost")).Content is not null)
        {
            throw new InvalidOperationException("При входе в раздел репозиториев должны открываться изменения, даже если ранее выбрана история.");
        }

        var list = (ListBox)repositoriesView.FindName("RepositoriesList");
        var groups = System.Windows.Data.CollectionViewSource.GetDefaultView(list.ItemsSource).Groups;
        if (groups?.Count != 1 || groups[0] is not System.Windows.Data.CollectionViewGroup { Name: "lio", ItemCount: 2 })
        {
            throw new InvalidOperationException("Репозитории общей папки должны находиться в одной группе со счётчиком.");
        }

        window.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(repositories[1]);
        if (row?.ContextMenu is not { } menu)
        {
            throw new InvalidOperationException("У строки репозитория должно быть стандартное контекстное меню.");
        }

        string? removed = null;
        repositoriesView.RemoveRepositoryRequested += path => removed = path;
        menu.PlacementTarget = row;
        menu.IsOpen = true;
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await InlineWorktreeUiChecks.WaitUntilAsync(() => repositoriesView.CanLeave);
        menu.IsOpen = false;
        if (removed != linked || list.Items.Count != 1 || !File.Exists(Path.Combine(linked, ".git")))
        {
            throw new InvalidOperationException("Меню должно убирать выбранную строку из списка, сохраняя рабочую копию на диске.");
        }

        Console.WriteLine("OK: вкладки клиентов независимы, репозитории сгруппированы, контекстное меню убирает запись без удаления файлов.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Клиент не завершил загрузку за отведённое время.");
            }

            await Task.Delay(25);
        }
    }

    internal static void RunAsync(Func<Task> action)
    {
        Exception? failure = null;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null)
        {
            throw new InvalidOperationException("Проверка встроенного worktree завершилась ошибкой.", failure);
        }
    }
}
