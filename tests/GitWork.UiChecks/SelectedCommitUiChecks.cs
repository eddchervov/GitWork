using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class SelectedCommitUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-selected-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        Window? window = null;
        try
        {
            var files = Task.Run(async () =>
            {
                await git.RunAsync(root, "init", "--initial-branch=main");
                await git.RunAsync(root, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(root, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "base\n");
                await File.WriteAllTextAsync(Path.Combine(root, "excluded.txt"), "base\n");
                await git.RunAsync(root, "add", "--all");
                await git.RunAsync(root, "commit", "-m", "base");
                await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "staged\n");
                await File.WriteAllTextAsync(Path.Combine(root, "excluded.txt"), "excluded staged\n");
                await git.RunAsync(root, "add", "--all");
                await File.WriteAllTextAsync(Path.Combine(root, "selected.txt"), "working\n");
                await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "new\n");

                return await git.GetStatusAsync(root, true);
            }).GetAwaiter().GetResult();
            var view = new ChangesView("Fixture", [new ChangeSource("Fixture", root, files)], git, "selected.txt", root, false);
            window = new Window { Content = view, Width = 1100, Height = 750 };
            window.Show();
            SelectedCommitUiChecks.RunAsync(async () =>
            {
                var list = (ListBox)view.FindName("FilesList");
                var excluded = list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.Path == "excluded.txt");
                excluded.IsIncluded = false;
                ((TextBox)view.FindName("CommitTitleBox")).Text = "UI selected commit";
                ((TextBox)view.FindName("CommitDescriptionBox")).Text = "Описание из формы";
                await view.RefreshAsync();
                if (list.Items.Count != 3 || list.Items.Cast<ChangeFileEntry>().Single(entry => entry.File.Path == "excluded.txt").IsIncluded
                    || ((TextBox)view.FindName("CommitTitleBox")).Text != "UI selected commit")
                {
                    throw new InvalidOperationException("Обновление должно сохранять галочки и черновик, включая staged-only файл.");
                }

                ((Button)view.FindName("CommitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (view.IsBusy && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                if (view.IsBusy || ((TextBlock)view.FindName("CommitStatusText")).Text != "Коммит создан."
                    || ((TextBox)view.FindName("CommitTitleBox")).Text.Length != 0
                    || list.Items.Count != 1 || ((ChangeFileEntry)list.Items[0]).File.Path != "excluded.txt"
                    || ((ChangeFileEntry)list.Items[0]).IsIncluded)
                {
                    throw new InvalidOperationException("Форма должна создать выбранный коммит, очистить сообщение и оставить исключённый файл.");
                }

                var message = await git.RunAsync(root, "log", "-1", "--format=%B");
                var selected = await git.RunAsync(root, "show", "HEAD:selected.txt");
                var staged = await git.RunAsync(root, "diff", "--cached", "--name-only");
                if (!message.Contains("Описание из формы", StringComparison.Ordinal) || selected.Trim() != "working"
                    || staged.Trim() != "excluded.txt")
                {
                    throw new InvalidOperationException("Коммит должен включать описание и полное рабочее содержимое, сохраняя индекс исключённого файла.");
                }
            });
            Console.WriteLine("OK: встроенная форма создаёт коммит по галочкам, сохраняет выбор при обновлении и исключённый staged-only файл.");
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
            throw new InvalidOperationException("Проверка формы коммита завершилась ошибкой.", failure);
        }
    }
}
