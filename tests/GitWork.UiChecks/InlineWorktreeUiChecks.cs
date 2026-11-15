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

                var contentWidth = host.ActualWidth;
                ((Button)view.FindName("ActionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.UpdateLayout();
                if (((FrameworkElement)view.FindName("ActionsPanel")).Visibility != Visibility.Visible || host.ActualWidth < contentWidth)
                {
                    throw new InvalidOperationException("Панель действий не должна сжимать diff во встроенном клиенте.");
                }
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
