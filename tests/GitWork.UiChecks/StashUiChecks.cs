using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class StashUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-stash-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        StashWindow? window = null;
        try
        {
            Task.Run(async () =>
            {
                await git.RunAsync(root, "init", "--initial-branch=main");
                await git.RunAsync(root, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(root, "config", "user.email", "ui@example.invalid");
                await git.RunAsync(root, "config", "core.autocrlf", "false");
                await File.WriteAllTextAsync(Path.Combine(root, "Example.cs"), "public class Example\n{\n    public int Value => 1;\n}\n");
                await git.RunAsync(root, "add", "--all");
                await git.RunAsync(root, "commit", "-m", "base");
                await File.WriteAllTextAsync(Path.Combine(root, "Example.cs"), "public class Example\n{\n    public int Value => 42;\n}\n");
                await git.RunAsync(root, "add", "--", "Example.cs");
                await File.WriteAllTextAsync(Path.Combine(root, "Example.cs"), "public class Example\n{\n    public int Value => 43;\n}\n");
                await File.WriteAllTextAsync(Path.Combine(root, "new-file.txt"), "new file kept by stash\n");
            }).GetAwaiter().GetResult();

            var stashWindow = new StashWindow(git, root);
            window = stashWindow;
            stashWindow.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                var list = (ListBox)stashWindow.FindName("StashesList");
                var files = (ListBox)stashWindow.FindName("FilesList");
                var viewer = (DiffView)stashWindow.FindName("DiffViewer");
                var message = (TextBox)stashWindow.FindName("MessageBox");
                var includeUntracked = (CheckBox)stashWindow.FindName("IncludeUntrackedBox");
                var scope = (ComboBox)stashWindow.FindName("ScopePicker");
                var status = (TextBlock)stashWindow.FindName("StatusText");
                await StashUiChecks.WaitUntilAsync(() => !stashWindow.IsBusy && list.ItemsSource is not null);
                if (list.Items.Count != 0 || includeUntracked.IsChecked == true)
                {
                    throw new InvalidOperationException("Новый список stash должен быть пустым, включение новых файлов выбирается явно.");
                }

                message.Text = "Перед переключением ветки";
                includeUntracked.IsChecked = true;
                ((Button)stashWindow.FindName("CreateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StashUiChecks.WaitUntilAsync(() => !stashWindow.IsBusy && list.Items.Count == 1 && files.Items.Count == 2
                    && viewer.Visibility == Visibility.Visible);
                if (!stashWindow.WorkingTreeChanged || (await git.GetStatusAsync(root, true)).Count != 0
                    || list.SelectedItem is not GitStashEntry { Message: "Перед переключением ветки" })
                {
                    throw new InvalidOperationException("Кнопка сохранения должна спрятать tracked и выбранные новые файлы с указанным названием.");
                }

                scope.SelectedIndex = 1;
                await StashUiChecks.WaitUntilAsync(() => files.Items.Count == 1 && viewer.Visibility == Visibility.Visible);
                var code = new System.Windows.Documents.TextRange(((RichTextBox)viewer.FindName("CodeViewer")).Document.ContentStart,
                    ((RichTextBox)viewer.FindName("CodeViewer")).Document.ContentEnd).Text;
                if (!code.Contains("42", StringComparison.Ordinal) || code.Contains("43", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Предпросмотр индекса должен показывать staged-версию, а не рабочий файл.");
                }

                scope.SelectedIndex = 0;
                await StashUiChecks.WaitUntilAsync(() => files.Items.Count == 2 && viewer.Visibility == Visibility.Visible);
                StashUiChecks.Render(stashWindow);
                files.SelectedItem = files.Items.Cast<ChangedFile>().Single(file => file.Path == "new-file.txt");
                await StashUiChecks.WaitUntilAsync(() => viewer.Visibility == Visibility.Visible && viewer.FilePath == "new-file.txt");
                if (((RichTextBox)viewer.FindName("CodeViewer")).Document.Blocks.Count == 0)
                {
                    throw new InvalidOperationException("Новые файлы сохранения должны иметь предпросмотр diff.");
                }

                ((Button)stashWindow.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StashUiChecks.WaitUntilAsync(() => !stashWindow.IsBusy);
                if (list.Items.Count != 1 || !File.Exists(Path.Combine(root, "new-file.txt"))
                    || !(await File.ReadAllTextAsync(Path.Combine(root, "Example.cs"))).Contains("43", StringComparison.Ordinal)
                    || !(await git.RunAsync(root, "show", ":Example.cs")).Contains("42", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Применить должно восстановить staged/unstaged версии и сохранить stash в списке.");
                }

                message.Text = "Второе сохранение";
                ((Button)stashWindow.FindName("CreateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StashUiChecks.WaitUntilAsync(() => !stashWindow.IsBusy && list.Items.Count == 2 && viewer.Visibility == Visibility.Visible);
                ((Button)stashWindow.FindName("PopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StashUiChecks.WaitUntilAsync(() => !stashWindow.IsBusy && list.Items.Count == 1 && viewer.Visibility == Visibility.Visible);
                if (!File.Exists(Path.Combine(root, "new-file.txt"))
                    || list.SelectedItem is not GitStashEntry { Message: "Перед переключением ветки" })
                {
                    throw new InvalidOperationException("Применить и удалить должно удалить только успешно применённое сохранение.");
                }

                var beforeRejectedApply = await File.ReadAllTextAsync(Path.Combine(root, "Example.cs"));
                ((Button)stashWindow.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await StashUiChecks.WaitUntilAsync(() => !stashWindow.IsBusy);
                if (status.Text == L10n.T("Готово") || list.Items.Count != 1
                    || await File.ReadAllTextAsync(Path.Combine(root, "Example.cs")) != beforeRejectedApply)
                {
                    throw new InvalidOperationException("Повторное применение поверх изменений должно показать ошибку, сохранив файлы и stash.");
                }
            });

            Console.WriteLine("OK: stash создаётся и просматривается в UI, индекс виден отдельно, apply сохраняет запись, pop удаляет только применённую.");
        }
        finally
        {
            window?.Close();
            var fullRoot = Path.GetFullPath(root);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("git-work-stash-ui-", StringComparison.Ordinal)
                && Directory.Exists(fullRoot))
            {
                InlineWorktreeUiChecks.RunAsync(async () =>
                {
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(fullRoot))
                            {
                                foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
                                {
                                    File.SetAttributes(file, FileAttributes.Normal);
                                }

                                Directory.Delete(fullRoot, true);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Проверка окна stash превысила время ожидания.");
            }

            await Task.Delay(15);
        }
    }

    private static void Render(Window window)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts");
        using var stream = File.Create(Path.GetFullPath("artifacts/stash-window-preview.png"));
        encoder.Save(stream);
    }
}
