using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class StartupUiChecks
{
    public static void Run()
    {
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "git-work-startup-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDirectory);
        var settingsOverride = typeof(AppSettings).GetProperty("SettingsDirectoryOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousSettingsDirectory = settingsOverride.GetValue(null);
        settingsOverride.SetValue(null, settingsDirectory);
        MainWindow? window = null;

        try
        {
            var repository = Path.Combine(settingsDirectory, "repositories", "Fixture");
            Directory.CreateDirectory(repository);
            var git = new GitService();
            Task.Run(async () =>
            {
                await git.RunAsync(repository, "init", "--initial-branch=main");
                await git.RunAsync(repository, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(repository, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "base\n");
                await git.RunAsync(repository, "add", "--all");
                await git.RunAsync(repository, "commit", "-m", "base");
                await git.RunAsync(repository, "worktree", "add", "-b", "task", Path.Combine(settingsDirectory, "task"));
            }).GetAwaiter().GetResult();

            var settings = new AppSettings
            {
                MainSection = "Worktrees",
                GitRepositoryPaths = [repository],
                RootPaths = [Path.GetDirectoryName(repository)!],
                AutoRefresh = false,
                LastUpdateCheckUtc = DateTimeOffset.UtcNow
            };
            File.WriteAllText(AppSettings.FilePath, JsonSerializer.Serialize(settings));

            var mainWindow = new MainWindow();
            window = mainWindow;
            mainWindow.Show();
            StartupUiChecks.RunAsync(async () =>
            {
                var status = (TextBlock)mainWindow.FindName("StatusText");
                var repositoriesSection = (RadioButton)mainWindow.FindName("RepositoriesSection");
                var worktreesSection = (RadioButton)mainWindow.FindName("WorktreesSection");
                var repositoriesHost = (ContentControl)mainWindow.FindName("GitClientHost");
                var workspace = (Grid)mainWindow.FindName("Workspace");
                var navigationCount = (TextBlock)mainWindow.FindName("NavigationCount");

                await StartupUiChecks.WaitUntilAsync(() => workspace.IsEnabled && status.Text == "Готово");

                if (repositoriesSection.IsChecked != true || worktreesSection.IsChecked == true
                    || repositoriesHost.Visibility != Visibility.Visible || workspace.Visibility != Visibility.Collapsed
                    || navigationCount.Text.Length != 0)
                {
                    throw new InvalidOperationException("При сохранённой вкладке Worktree приложение должно сразу открыть репозитории и отложить поиск копий.");
                }

                await StartupUiChecks.CheckStatusAsync(mainWindow, (GitClientView)repositoriesHost.Content);
                worktreesSection.IsChecked = true;
                if (status.Text != L10n.T("Обновление списка…")
                    || mainWindow.FindName("BusyProgress") is not FrameworkElement { Visibility: Visibility.Visible })
                {
                    throw new InvalidOperationException("Общая загрузка раздела должна отображаться в нижней строке состояния.");
                }

                await StartupUiChecks.WaitUntilAsync(() =>
                {
                    return navigationCount.Text.Length > 0 && workspace.IsEnabled;
                });

                if (worktreesSection.IsChecked != true || workspace.Visibility != Visibility.Visible)
                {
                    throw new InvalidOperationException("Первая навигация на вкладку Worktree должна запускать поиск рабочих копий.");
                }

                await StartupUiChecks.CheckStatusAsync(mainWindow, (GitClientView)((ContentControl)mainWindow.FindName("InlineGitHost")).Content);
                var setStatus = typeof(GitClientView).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
                setStatus.Invoke(repositoriesHost.Content, ["Сообщение скрытого клиента"]);
                if (status.Text != L10n.T("Готово"))
                {
                    throw new InvalidOperationException("Скрытый клиент не должен перезаписывать статус открытого раздела.");
                }

                setStatus.Invoke(repositoriesHost.Content, [""]);
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    repositoriesSection.IsChecked = true;
                    worktreesSection.IsChecked = true;
                    repositoriesSection.IsChecked = true;
                    await StartupUiChecks.WaitUntilAsync(() => workspace.IsEnabled);
                    StartupUiChecks.AssertSection(repositoriesSection, worktreesSection, repositoriesHost, workspace);

                    worktreesSection.IsChecked = true;
                    repositoriesSection.IsChecked = true;
                    worktreesSection.IsChecked = true;
                    await StartupUiChecks.WaitUntilAsync(() => workspace.IsEnabled);
                    StartupUiChecks.AssertSection(worktreesSection, repositoriesSection, workspace, repositoriesHost);
                    await Task.Delay(100);
                    StartupUiChecks.AssertSection(worktreesSection, repositoriesSection, workspace, repositoriesHost);
                    if (mainWindow.FindName("InlineGitHost") is not ContentControl { Visibility: Visibility.Visible, Content: GitClientView })
                    {
                        throw new InvalidOperationException("После возврата на Worktree должна быть открыта выбранная рабочая копия.");
                    }
                }

                var busyField = typeof(MainWindow).GetField("_isBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
                busyField.SetValue(mainWindow, true);
                repositoriesSection.IsChecked = true;
                await mainWindow.Dispatcher.InvokeAsync(() => { });
                StartupUiChecks.AssertSection(worktreesSection, repositoriesSection, workspace, repositoriesHost);
                busyField.SetValue(mainWindow, false);
                repositoriesSection.IsChecked = true;
                await StartupUiChecks.WaitUntilAsync(() => workspace.IsEnabled);
                StartupUiChecks.AssertSection(repositoriesSection, worktreesSection, repositoriesHost, workspace);
            });

            Console.WriteLine("OK: приложение открывает репозитории, ищет worktree при переходе и сохраняет последний выбор главной вкладки.");
        }
        finally
        {
            window?.Close();
            settingsOverride.SetValue(null, previousSettingsDirectory);
            if (Directory.Exists(settingsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(settingsDirectory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                StartupUiChecks.RunAsync(async () =>
                {
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(settingsDirectory))
                            {
                                Directory.Delete(settingsDirectory, true);
                            }

                            return;
                        }
                        catch (IOException) when (attempt < 19)
                        {
                            await Task.Delay(50);
                        }
                    }
                });
            }
        }
    }

    private static async Task CheckStatusAsync(MainWindow window, GitClientView client)
    {
        var status = (TextBlock)window.FindName("StatusText");
        var progress = (FrameworkElement)window.FindName("BusyProgress");
        var run = typeof(GitClientView).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = (Task)run.Invoke(client, [new Func<Task>(() => completion.Task)])!;
        if (status.Text != L10n.T("Загрузка…") || progress.Visibility != Visibility.Visible
            || client.FindName("ErrorText") is not null || window.FindName("OperationOverlay") is not null)
        {
            completion.SetResult();
            await operation;

            throw new InvalidOperationException("Сообщение загрузки и индикатор должны отображаться только в нижней строке состояния.");
        }

        const string error = "Не удалось прочитать тестовый репозиторий.";
        completion.SetException(new GitException(error));
        await operation;
        if (status.Text != error || status.ToolTip as string != error || progress.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("Ошибка должна оставаться доступной в нижней строке состояния после завершения загрузки.");
        }

        var execute = typeof(MainWindow).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const string completedMessage = "Общая операция завершена.";
        await (Task)execute.Invoke(window, [new Func<Task>(() =>
        {
            status.Text = completedMessage;

            return Task.CompletedTask;
        }), "Проверка общего статуса"])!;
        if (status.Text != completedMessage || status.ToolTip is not null)
        {
            throw new InvalidOperationException("Старая ошибка клиента не должна подменять результат общей операции.");
        }

        var setStatus = typeof(GitClientView).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)run.Invoke(client, [new Func<Task>(() =>
        {
            setStatus.Invoke(client, [error]);

            return Task.CompletedTask;
        })])!;
        if (status.Text != error)
        {
            throw new InvalidOperationException("Ошибка, обработанная внутри загрузки, не должна стираться при её завершении.");
        }

        await (Task)run.Invoke(client, [new Func<Task>(() => Task.CompletedTask)])!;
        if (status.Text != L10n.T("Готово") || progress.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("После успешной операции нижняя строка должна возвращаться в состояние готовности.");
        }

        await (Task)execute.Invoke(window, [new Func<Task>(() => Task.CompletedTask), "Проверка общего статуса"])!;
        if (status.Text != L10n.T("Готово") || status.ToolTip is not null)
        {
            throw new InvalidOperationException("При завершении общей операции подсказка не должна хранить старый текст загрузки.");
        }
    }

    private static void AssertSection(RadioButton selected, RadioButton other, FrameworkElement visible, FrameworkElement hidden)
    {
        if (selected.IsChecked != true || other.IsChecked == true
            || visible.Visibility != Visibility.Visible || hidden.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("Выделение главной вкладки должно соответствовать открытому разделу после быстрых переключений.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Проверка запуска приложения превысила время ожидания.");
            }

            await Task.Delay(25);
        }
    }

    private static void RunAsync(Func<Task> action)
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
            throw new InvalidOperationException("Проверка экрана запуска завершилась ошибкой.", failure);
        }
    }
}
