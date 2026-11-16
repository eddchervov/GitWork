using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class RepositoryClientUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-repository-ui-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "lio-app");
        var second = Path.Combine(root, "lio");
        var linked = Path.Combine(root, "occupied");
        var git = new GitService();
        Window? window = null;
        try
        {
            Task.Run(async () =>
            {
                foreach (var path in new[] { first, second })
                {
                    Directory.CreateDirectory(path);
                    await git.RunAsync(path, "init", "--initial-branch=main");
                    await git.RunAsync(path, "config", "user.name", "Git Work UI checks");
                    await git.RunAsync(path, "config", "user.email", "ui@example.invalid");
                    await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "Fixture\n");
                    await git.RunAsync(path, "add", "--all");
                    await git.RunAsync(path, "commit", "-m", "Fixture");
                }

                await git.RunAsync(first, "branch", "feature/new-ui");
                await git.RunAsync(first, "worktree", "add", "-b", "feature/occupied", linked);
                await git.RunAsync(first, "update-ref", "refs/remotes/origin/release/test", "HEAD");
                await File.WriteAllTextAsync(Path.Combine(first, "local.txt"), "Uncommitted\n");
            }).GetAwaiter().GetResult();

            var view = new GitClientView(git, new AppSettings { AutoRefresh = false, SelectedRepository = first });
            window = new Window { Content = view, Width = 1380, Height = 860 };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                var firstRepository = new RepositoryInfo("lio-app", first, "Lio");
                var secondRepository = new RepositoryInfo("lio", second, "Lio");
                await view.SetRepositoriesAsync([firstRepository, secondRepository]);
                window.UpdateLayout();
                var list = (ListBox)view.FindName("RepositoriesList");
                var firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(firstRepository);
                var secondRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(secondRepository);
                await RepositoryClientUiChecks.WaitUntilAsync(() => firstRow.Tag is RepositoryRowState { ChangeCount: 1 }
                    && secondRow.Tag is RepositoryRowState { Branch: "main" });
                if (firstRow.Tag is not RepositoryRowState { HasChanges: true, ChangeCount: 1, Branch: "main" }
                    || secondRow.Tag is not RepositoryRowState { HasChanges: false, HasError: false })
                {
                    throw new InvalidOperationException("Список должен показывать ветку и счётчик незакоммиченных файлов, скрывая отметку чистого репозитория.");
                }

                var trigger = (Button)view.FindName("BranchSelectorButton");
                var host = (FrameworkElement)view.FindName("BranchPickerHost");
                var picker = (BranchPickerView)view.FindName("BranchPicker");
                var branches = (ListBox)picker.FindName("BranchesList");
                var content = (ContentControl)view.FindName("ViewHost");
                var width = content.ActualWidth;
                trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await RepositoryClientUiChecks.WaitUntilAsync(() => view.CanLeave);
                window.UpdateLayout();
                if (host.Visibility != Visibility.Visible || host.ActualHeight < view.ActualHeight * 0.65
                    || content.ActualWidth != width
                    || ((TextBlock)picker.FindName("CurrentBranchLabel")).Text != "main"
                    || ((FrameworkElement)picker.FindName("DirtyWarning")).Visibility != Visibility.Visible
                    || branches.IsEnabled
                    || branches.Items.Cast<BranchPickerChoice>().Any(choice => !choice.Branch.IsRemote && choice.Branch.Name == "main"))
                {
                    throw new InvalidOperationException("Панель должна занимать высоту окна поверх diff, закреплять реальную ветку и запрещать переключение при изменениях.");
                }

                picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                if (host.Visibility != Visibility.Collapsed)
                {
                    throw new InvalidOperationException("Escape должен закрывать панель веток.");
                }

                File.Delete(Path.Combine(first, "local.txt"));
                trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await RepositoryClientUiChecks.WaitUntilAsync(() => view.CanLeave);
                window.UpdateLayout();
                if (!branches.IsEnabled || firstRow.Tag is not RepositoryRowState { HasChanges: false })
                {
                    throw new InvalidOperationException("Перед открытием веток нужно перечитать Git и убрать устаревший счётчик изменений.");
                }

                var occupied = branches.Items.Cast<BranchPickerChoice>().Single(choice => choice.Branch.Name == "feature/occupied");
                if (occupied.IsSelectable || !branches.Items.Cast<BranchPickerChoice>().Any(choice => choice.Branch.IsRemote))
                {
                    throw new InvalidOperationException("Занятая другим worktree ветка недоступна, а удалённые ветки видны в том же списке.");
                }

                var search = (TextBox)picker.FindName("SearchBox");
                search.Text = "new-ui";
                if (branches.Items.Count != 1)
                {
                    throw new InvalidOperationException("Поиск должен фильтровать ветки без дополнительных переключателей групп.");
                }

                branches.SelectedIndex = 0;
                if ((await git.RunAsync(first, "branch", "--show-current")).Trim() != "main")
                {
                    throw new InvalidOperationException("Навигация по списку с клавиатуры не должна сама переключать ветку.");
                }

                branches.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(branches)!, Environment.TickCount, Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                await RepositoryClientUiChecks.WaitUntilAsync(() => view.CanLeave);
                if ((await git.RunAsync(first, "branch", "--show-current")).Trim() != "feature/new-ui"
                    || ((TextBlock)view.FindName("CurrentBranchLabel")).Text != "feature/new-ui"
                    || host.Visibility != Visibility.Collapsed)
                {
                    throw new InvalidOperationException("Enter должен сразу переключить доступную ветку, закрыть панель и обновить текущую ветку.");
                }

                await File.WriteAllTextAsync(Path.Combine(second, "local.txt"), "Uncommitted\n");
                await view.RefreshAsync();
                if (secondRow.Tag is not RepositoryRowState { HasChanges: true, ChangeCount: 1 })
                {
                    throw new InvalidOperationException("Обновление должно перечитывать изменения и у невыбранных репозиториев.");
                }

                var remote = new BranchInfo("origin/release/test", "refs/remotes/origin/release/test", true, null);
                var remoteDialog = new CreateBranchWindow(first, [remote], "feature/new-ui", remote.FullName);
                if (remoteDialog.StartPoint != remote.FullName)
                {
                    throw new InvalidOperationException("Создание локальной ветки от удалённой должно сразу выбирать правильный исходный ref.");
                }

                remoteDialog.Close();
                var historyTab = (RadioButton)view.FindName("HistoryTab");
                historyTab.IsChecked = true;
                await RepositoryClientUiChecks.WaitUntilAsync(() => content.Content is GitHistoryView { IsBusy: false });
                var history = (GitHistoryView)content.Content;
                var commitSearch = (TextBox)history.FindName("SearchBox");
                var commits = (ListBox)history.FindName("CommitsList");
                commitSearch.Text = "missing-commit";
                await history.RefreshAsync();
                if (commits.SelectedItem is not null || ((TextBlock)history.FindName("SearchResultText")).Text.Length == 0)
                {
                    throw new InvalidOperationException("Обновление истории должно сохранять активный поиск без совпадений.");
                }

                commitSearch.Clear();
                if (commits.SelectedItem is null)
                {
                    throw new InvalidOperationException("Очистка поиска должна восстановить выбранный коммит и его сведения.");
                }

                commitSearch.Text = "Fixture";
                await history.RefreshAsync();
                if (commits.SelectedItem is not GraphCommitItem { Row.Commit.Subject: "Fixture" })
                {
                    throw new InvalidOperationException("После обновления истории должен оставаться выбран коммит, найденный поиском.");
                }

                ((RadioButton)view.FindName("ChangesTab")).IsChecked = true;
                await RepositoryClientUiChecks.WaitUntilAsync(() => view.CanLeave);
                trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await RepositoryClientUiChecks.WaitUntilAsync(() => view.CanLeave);
                window.UpdateLayout();
                var output = Path.GetFullPath("artifacts/repository-client-preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(output);
                encoder.Save(stream);
            });
            Console.WriteLine("OK: счётчики репозиториев, высокая панель веток, поиск, защита dirty/occupied, Escape и переключение Enter.");
        }
        finally
        {
            window?.Close();
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Клиент не завершил операцию за отведённое время.");
            }

            await Task.Delay(25);
        }
    }
}
