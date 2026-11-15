using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class CommitDialogChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "git-work-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var git = new GitService();
        var operations = new GitCommitOperationService(git);
        try
        {
            var preview = Task.Run(async () =>
            {
                await git.RunAsync(root, "init", "--initial-branch=main");
                await git.RunAsync(root, "config", "user.name", "Git Work UI Test");
                await git.RunAsync(root, "config", "user.email", "ui@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "base\n");
                await git.RunAsync(root, "add", "--", "file.txt");
                await git.RunAsync(root, "commit", "-m", "base");
                var hash = (await git.RunAsync(root, "rev-parse", "HEAD")).Trim();

                return await operations.PreviewAsync(root, hash);
            }).GetAwaiter().GetResult();

            var trees = new[] { new WorktreeInfo(root, preview.CommitHash, "main", true, false, false, false, null) };
            var branches = new[]
            {
                new BranchInfo("main", "refs/heads/main", false, root),
                new BranchInfo("other", "refs/heads/other", false, null),
                new BranchInfo("busy", "refs/heads/busy", false, Path.Combine(root, "other-worktree"))
            };

            var dialog = new CommitOperationWindow(CommitOperationKind.Branch, root, git, operations, preview, branches, trees, "main", preview.CommitHash);
            var timedOut = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (_, _) =>
            {
                timedOut = true;
                dialog.Close();
            };
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                ((TextBox)dialog.FindName("BranchName")).Text = "from-ui";
                ((Button)dialog.FindName("RunButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            timer.Start();
            var accepted = dialog.ShowDialog();
            timer.Stop();
            if (timedOut || accepted != true || dialog.Result is null)
            {
                throw new InvalidOperationException("Успешное создание ветки должно закрыть модальное окно ровно после завершения операции.");
            }

            var branchHash = Task.Run(async () => (await git.RunAsync(root, "rev-parse", "refs/heads/from-ui")).Trim()).GetAwaiter().GetResult();
            if (branchHash != preview.CommitHash)
            {
                throw new InvalidOperationException("Модальное окно должно создавать ветку от выбранного коммита.");
            }

            var cherry = new CommitOperationWindow(CommitOperationKind.CherryPick, root, git, operations, preview, branches, trees, "main", preview.CommitHash);
            try
            {
                cherry.Show();
                var picker = (ComboBox)cherry.FindName("TargetBranch");
                picker.IsDropDownOpen = true;
                picker.UpdateLayout();
                cherry.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                if (picker.ItemContainerGenerator.ContainerFromIndex(0) is not ComboBoxItem { IsEnabled: true }
                    || picker.ItemContainerGenerator.ContainerFromIndex(2) is not ComboBoxItem { IsEnabled: false })
                {
                    throw new InvalidOperationException("Текущую ветку можно выбрать целью cherry-pick, занятую другой копией — нельзя.");
                }

                picker.IsDropDownOpen = false;
                picker.SelectedIndex = 1;
                picker.SelectedIndex = 0;
                if (picker.SelectedItem is not CherryPickBranchChoice { Branch.Name: "main" })
                {
                    throw new InvalidOperationException("После выбора другой ветки должна оставаться возможность вернуться к текущей.");
                }
            }
            finally
            {
                cherry.Close();
            }

            Console.WriteLine("OK: модальная операция создаёт ветку и закрывается; cherry-pick позволяет выбрать текущую ветку и защищает занятые worktree.");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, true);
        }
    }
}
