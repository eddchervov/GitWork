using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class BranchPublicationUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-publish-ui-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(repository);
        var git = new GitService();
        Window? window = null;
        try
        {
            Task.Run(async () =>
            {
                await git.RunAsync(repository, "init", "--initial-branch=main");
                await git.RunAsync(repository, "config", "user.name", "Git Work UI checks");
                await git.RunAsync(repository, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(repository, "sample.txt"), "base\n");
                await git.RunAsync(repository, "add", "--all");
                await git.RunAsync(repository, "commit", "-m", "base");
                await git.RunAsync(root, "init", "--bare", remote);
                await git.RunAsync(repository, "remote", "add", "origin", remote);
            }).GetAwaiter().GetResult();

            var client = new GitClientView(git, new AppSettings { AutoRefresh = false });
            window = new Window { Content = client, Width = 1100, Height = 740, ShowInTaskbar = false };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                await client.SetRepositoriesAsync([new RepositoryInfo("Fixture", repository, "Fixture")]);
                var initial = await new GitRemoteService(git).ReadAsync(repository);
                var credentialsPreview = new PublishBranchWindow(git, initial with
                {
                    Remotes = [new GitRemoteInfo("origin", "https://user:fixture-token@example.invalid/repo",
                        "https://user:fixture-token@example.invalid/repo")]
                });
                if (((TextBlock)credentialsPreview.FindName("DestinationText")).Text.Contains("fixture-token", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Подтверждение публикации не должно показывать токен из адреса сервера.");
                }

                credentialsPreview.Close();
                var push = (Button)client.FindName("SyncPushButton");
                var pull = (Button)client.FindName("SyncPullButton");
                await BranchPublicationUiChecks.WaitUntilAsync(() => client.CanLeave && push.IsEnabled);
                if (!Equals(push.Content, "Опубликовать ветку…") || pull.Visibility != Visibility.Collapsed)
                {
                    throw new InvalidOperationException("Ветка без upstream должна предлагать публикацию вместо отключённого Push.");
                }

                BranchPublicationUiChecks.HandleDialog(push, dialog =>
                {
                    var names = (TextBox)dialog.FindName("BranchNameBox");
                    var remotes = (ComboBox)dialog.FindName("RemotePicker");
                    if (names.Text != "main" || remotes.SelectedItem is not GitRemoteInfo { Name: "origin" }
                        || !((TextBlock)dialog.FindName("DestinationText")).Text.Contains(remote, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Диалог должен показывать локальную ветку, выбранный сервер и адрес отправки.");
                    }

                    dialog.Close();
                });
                if ((await git.RunAsync(remote, "for-each-ref", "--format=%(refname)", "refs/heads")).Length != 0
                    || (await new GitRemoteService(git).ReadAsync(repository)).UpstreamRemote is not null)
                {
                    throw new InvalidOperationException("Отмена диалога не должна публиковать ветку или настраивать upstream.");
                }

                BranchPublicationUiChecks.HandleDialog(push, dialog =>
                {
                    ((TextBox)dialog.FindName("BranchNameBox")).Text = "feature/published";
                    ((Button)dialog.FindName("PublishButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                await BranchPublicationUiChecks.WaitUntilAsync(() => client.CanLeave && !Equals(push.Content, "Опубликовать ветку…"));
                var state = await new GitRemoteService(git).ReadAsync(repository);
                var published = (await git.RunAsync(remote, "rev-parse", "refs/heads/feature/published")).Trim();
                if (state.UpstreamRemote != "origin" || state.UpstreamBranch != "feature/published"
                    || state.Head != published || state.Ahead != 0 || state.Behind != 0 || state.CanPublish
                    || pull.Visibility != Visibility.Visible)
                {
                    throw new InvalidOperationException("Подтверждение должно создать только выбранную ветку и переключить интерфейс на обычный Push.");
                }
            });
        }
        finally
        {
            window?.Close();
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("GitWork-publish-ui-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Небезопасный путь временной проверки.");
            }

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

        Console.WriteLine("OK: публикация запрашивает сервер и имя, отмена сохраняет состояние, подтверждение создаёт ветку и upstream.");
    }

    private static void HandleDialog(Button button, Action<PublishBranchWindow> action)
    {
        Exception? error = null;
        var handled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<PublishBranchWindow>().FirstOrDefault();
            if (dialog is null)
            {
                return;
            }

            timer.Stop();
            handled = true;
            try
            {
                action(dialog);
            }
            catch (Exception exception)
            {
                error = exception;
                dialog.Close();
            }
        };
        timer.Start();
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            timer.Stop();
        }

        if (error is not null)
        {
            throw new InvalidOperationException("Ошибка проверки диалога публикации.", error);
        }

        if (!handled)
        {
            throw new InvalidOperationException("Кнопка должна открыть диалог подтверждения публикации.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("Истекло время ожидания публикации тестовой ветки.");
            }

            await Task.Delay(30);
        }
    }
}
