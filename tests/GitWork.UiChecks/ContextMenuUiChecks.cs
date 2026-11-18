using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class ContextMenuUiChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-context-menu-ui-" + Guid.NewGuid().ToString("N"));
        var repositoryA = Path.Combine(root, "repository-a");
        var repositoryB = Path.Combine(root, "repository-b");
        var linkedA = Path.Combine(root, "linked-a");
        Directory.CreateDirectory(repositoryA);
        Directory.CreateDirectory(repositoryB);
        var git = new GitService();
        var clientSettings = new AppSettings { GitRepositoryPaths = [], SelectedRepository = repositoryA, AutoRefresh = false };
        var choices = new[]
        {
            new RepositoryInfo("Repository A", repositoryA, "Fixtures"),
            new RepositoryInfo("Repository B", repositoryB, "Fixtures")
        };
        const string rootMessage = "B root commit\n\nFirst body paragraph.";
        const string secondMessage = "B second commit\n\nFull body line one.\nFull body line two.";
        Window? window = null;

        try
        {
            var hashes = Task.Run(async () =>
            {
                await ContextMenuUiChecks.InitializeRepositoryAsync(git, repositoryA, "A-only commit");
                await git.RunAsync(repositoryA, "worktree", "add", "-b", "linked", linkedA);
                await ContextMenuUiChecks.InitializeRepositoryAsync(git, repositoryB, rootMessage);
                var rootHash = (await git.RunAsync(repositoryB, "rev-parse", "HEAD")).Trim();
                await File.WriteAllTextAsync(Path.Combine(repositoryB, "second.txt"), "second\n");
                await git.RunAsync(repositoryB, "add", "--all");
                await git.RunAsync(repositoryB, "commit", "-m", secondMessage);
                var secondHash = (await git.RunAsync(repositoryB, "rev-parse", "HEAD")).Trim();
                return (rootHash, secondHash);
            }).GetAwaiter().GetResult();

            var client = new GitClientView(git, clientSettings);
            window = new Window { Content = client, Width = 1100, Height = 760 };
            window.Show();
            InlineWorktreeUiChecks.RunAsync(async () =>
            {
                await client.SetRepositoriesAsync(choices);
                await ContextMenuUiChecks.WaitUntilAsync(() => client.CanLeave);
                await client.OpenCopyAsync(choices[0], linkedA, "Changes", workspaceContext: false);
                await ContextMenuUiChecks.WaitUntilAsync(() => client.CanLeave);

                var repositoryList = (ListBox)client.FindName("RepositoriesList");
                var repositoryHeader = (TextBox)client.FindName("RepositoryName");
                window.UpdateLayout();

                var headerMenu = repositoryHeader.ContextMenu
                    ?? throw new InvalidOperationException("Заголовок репозитория должен иметь контекстное меню.");
                headerMenu.IsOpen = true;
                var headerLocation = (TextBlock)((MenuItem)headerMenu.Items[1]).Header;
                var headerBranch = (TextBlock)((MenuItem)headerMenu.Items[2]).Header;
                if (!GitService.PathsEqual(headerLocation.Text, linkedA) || !headerBranch.Text.Contains("linked", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Меню заголовка должно показывать путь и ветку выбранного worktree.");
                }

                headerMenu.IsOpen = false;

                var rowB = (ListBoxItem?)repositoryList.ItemContainerGenerator.ContainerFromItem(choices[1])
                    ?? throw new InvalidOperationException("Для второго репозитория должен быть создан элемент списка.");
                var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent,
                    Source = rowB
                };

                rowB.RaiseEvent(rightClick);
                if (!rightClick.Handled)
                {
                    rowB.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    {
                        RoutedEvent = Mouse.MouseDownEvent,
                        Source = rowB
                    });
                }

                if (!client.CanLeave || repositoryList.SelectedItem is not RepositoryInfo activeRepository
                    || !GitService.PathsEqual(activeRepository.Path, repositoryA))
                {
                    throw new InvalidOperationException("Нажатие правой кнопки не должно запускать загрузку до открытия меню.");
                }

                rowB.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent,
                    Source = rowB
                });
                var repositoryMenu = rowB.ContextMenu
                    ?? throw new InvalidOperationException("У строки репозитория должно быть контекстное меню.");
                if (!repositoryMenu.IsOpen)
                {
                    throw new InvalidOperationException("Полный первый правый клик должен сразу открывать меню репозитория.");
                }
                if (repositoryList.SelectedItem is not RepositoryInfo clickedRepository
                    || !GitService.PathsEqual(clickedRepository.Path, repositoryB))
                {
                    throw new InvalidOperationException("Первый правый клик должен выбирать репозиторий B и сразу открывать его меню.");
                }
                var repositoryActions = repositoryMenu.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
                if (!repositoryActions.Any(item => Equals(item.Tag, "remove-repository"))
                    || !repositoryActions.Any(item => Equals(item.Tag, "repository-info")))
                {
                    throw new InvalidOperationException("Меню строки репозитория должно содержать сведения и удаление из списка.");
                }

                var repositoryInfoAction = repositoryActions.Single(item => Equals(item.Tag, "repository-info"));
                repositoryMenu.IsOpen = false;
                var repositoryInformation = await ContextMenuUiChecks.CaptureInformationAsync(
                    () => repositoryInfoAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                if (!repositoryInformation.Contains("Путь: " + repositoryB, StringComparison.Ordinal)
                    || !repositoryInformation.Contains("Ветка: main", StringComparison.Ordinal)
                    || repositoryInformation.Contains("Путь: " + repositoryA, StringComparison.Ordinal)
                    || repositoryList.SelectedItem is not RepositoryInfo selectedRepository
                    || !GitService.PathsEqual(selectedRepository.Path, repositoryB))
                {
                    throw new InvalidOperationException("Сведения должны относиться к выбранному правой кнопкой репозиторию B, в том числе при запросе во время загрузки.");
                }

                repositoryMenu.IsOpen = true;
                var historyAction = repositoryMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "history"));
                repositoryMenu.IsOpen = false;
                historyAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await ContextMenuUiChecks.WaitUntilAsync(() => repositoryList.SelectedItem is RepositoryInfo current
                    && GitService.PathsEqual(current.Path, repositoryB)
                    && ((ContentControl)client.FindName("ViewHost")).Content is GitHistoryView history
                    && !history.IsBusy
                    && ((ListBox)history.FindName("CommitsList")).Items.Count >= 2);

                var historyView = (GitHistoryView)((ContentControl)client.FindName("ViewHost")).Content;
                if (((RadioButton)client.FindName("HistoryTab")).IsChecked != true
                    || ((RadioButton)client.FindName("ChangesTab")).IsChecked == true
                    || repositoryList.SelectedItem is not RepositoryInfo historyRepository
                    || !GitService.PathsEqual(historyRepository.Path, repositoryB))
                {
                    throw new InvalidOperationException("Действие «Открыть историю» должно выбрать историю именно для репозитория B.");
                }

                var commits = (ListBox)historyView.FindName("CommitsList");
                var rootCommit = commits.Items.OfType<GraphCommitItem>().Single(item => item.Row.Commit.Hash == hashes.rootHash);
                var secondCommit = commits.Items.OfType<GraphCommitItem>().Single(item => item.Row.Commit.Hash == hashes.secondHash);
                commits.SelectedItem = secondCommit;
                commits.ScrollIntoView(rootCommit);
                historyView.UpdateLayout();
                var rootRow = (ListBoxItem?)commits.ItemContainerGenerator.ContainerFromItem(rootCommit)
                    ?? throw new InvalidOperationException("Строка корневого коммита должна быть создана.");
                rootRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent,
                    Source = rootRow
                });
                if (!Equals(commits.SelectedItem, rootCommit))
                {
                    throw new InvalidOperationException("Щелчок правой кнопкой должен выбирать именно целевую строку коммита.");
                }

                var commitMenu = commits.ContextMenu
                    ?? throw new InvalidOperationException("Список коммитов должен иметь контекстное меню.");
                commitMenu.PlacementTarget = commits;
                commitMenu.IsOpen = true;
                commitMenu.GetBindingExpression(FrameworkElement.DataContextProperty)?.UpdateTarget();
                if (commitMenu.DataContext is not GraphCommitItem selectedCommit || selectedCommit.Row.Commit.Hash != hashes.rootHash)
                {
                    throw new InvalidOperationException("Контекстное меню должно быть связано с выбранным правой кнопкой коммитом.");
                }

                var commitInfoAction = commitMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "commit-info"));
                commitMenu.IsOpen = false;
                var commitInformation = await ContextMenuUiChecks.CaptureInformationAsync(
                    () => commitInfoAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                if (!commitInformation.Contains("Хеш: " + hashes.rootHash, StringComparison.Ordinal)
                    || !commitInformation.Contains("Автор: B Author <b@example.invalid>", StringComparison.Ordinal)
                    || !commitInformation.Contains(rootMessage, StringComparison.Ordinal)
                    || !commitInformation.Contains("Родительские коммиты: " + Environment.NewLine, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Сведения о корневом коммите должны показывать правильные автора, сообщение, хеш и отсутствие родителей.");
                }

                var details = new GitCommitDetailsService(git);
                var secondInformation = await details.GetInformationAsync(repositoryB, hashes.secondHash);
                var rootInformation = await details.GetInformationAsync(repositoryB, hashes.rootHash);
                if (secondInformation.Message != secondMessage || secondInformation.Author != "B Author"
                    || secondInformation.AuthorEmail != "b@example.invalid" || secondInformation.Parents != hashes.rootHash
                    || rootInformation.Message != rootMessage || rootInformation.Parents.Length != 0)
                {
                    throw new InvalidOperationException("Git должен возвращать полное сообщение, автора и родителей, включая корневой коммит без родителей.");
                }

                try
                {
                    await details.GetInformationAsync(repositoryB, "invalid-hash");
                    throw new InvalidOperationException("Недопустимый хеш не должен открывать сведения о коммите.");
                }
                catch (GitException)
                {
                }
            });

            Console.WriteLine("OK: первый правый клик выбирает репозиторий и открывает меню; действия дожидаются загрузки, сведения коммитов полные.");
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

    private static async Task InitializeRepositoryAsync(GitService git, string path, string message)
    {
        await git.RunAsync(path, "init", "--initial-branch=main");
        await git.RunAsync(path, "config", "user.name", "B Author");
        await git.RunAsync(path, "config", "user.email", "b@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(path, "root.txt"), message + "\n");
        await git.RunAsync(path, "add", "--all");
        await git.RunAsync(path, "commit", "-m", message);
    }

    private static async Task<string> CaptureInformationAsync(Action open)
    {
        string? information = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<InformationWindow>().FirstOrDefault() is not { } dialog)
            {
                return;
            }

            information = ((TextBox)dialog.FindName("DetailsText")).Text;
            dialog.Close();
            timer.Stop();
        };
        timer.Start();

        try
        {
            open();
            await ContextMenuUiChecks.WaitUntilAsync(() => information is not null);
            return information!;
        }
        finally
        {
            timer.Stop();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Контекстное меню не завершило действие за отведённое время.");
            }

            await Task.Delay(25);
        }
    }
}
