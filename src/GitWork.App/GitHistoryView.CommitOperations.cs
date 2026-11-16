using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitWork.Core;

namespace GitWork.App;

public partial class GitHistoryView
{
    private void Commit_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (this._isBusy)
        {
            e.Handled = true;
            return;
        }

        GitHistoryView.SelectContainer(e.OriginalSource as DependencyObject);
    }

    private void Commit_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => GitHistoryView.SelectContainer(e.NewFocus as DependencyObject);

    private static void SelectContainer(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBoxItem item)
            {
                item.IsSelected = true;
                return;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
    }

    private async void BranchFromCommit_Click(object sender, RoutedEventArgs e)
    {
        this.SelectContextCommit(sender);
        await this.OpenOperationAsync(CommitOperationKind.Branch);
    }

    private async void WorktreeFromCommit_Click(object sender, RoutedEventArgs e)
    {
        this.SelectContextCommit(sender);
        await this.OpenOperationAsync(CommitOperationKind.Worktree);
    }

    private async void CherryPick_Click(object sender, RoutedEventArgs e)
    {
        this.SelectContextCommit(sender);
        await this.OpenOperationAsync(CommitOperationKind.CherryPick);
    }

    private void SelectContextCommit(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { DataContext: GraphCommitItem item } })
        {
            this.CommitsList.SelectedItem = item;
        }
    }

    private async Task OpenOperationAsync(CommitOperationKind kind)
    {
        if (this._isBusy || this.SelectedRepository is not { } repository || this.CommitsList.SelectedItem is not GraphCommitItem item)
        {
            return;
        }

        this.SetBusy(true);
        this.OperationStatus.Text = L10n.T("Подготовка операции…");

        try
        {
            var preview = await this._operations.PreviewAsync(repository.Path, item.Row.Commit.Hash);
            var worktrees = await this._git.GetWorktreesAsync(repository.Path);
            var branches = await this._git.GetBranchesAsync(repository.Path, worktrees);
            var branch = preview.CurrentBranch;
            var head = (await this._git.RunAsync(repository.Path, "rev-parse", "--verify", "HEAD")).TrimEnd('\r', '\n');
            if (this._isClosed)
            {
                return;
            }

            var dialog = new CommitOperationWindow(kind, repository.Path, this._git, this._operations, preview,
                branches, worktrees, branch, head, this._preparationSettings)
            {
                Owner = Window.GetWindow(this)
            };
            this.SetBusy(false);
            dialog.ShowDialog();
            if (dialog.CreatedWorktreePath is { } createdPath)
            {
                PreparationLauncher.Show(Window.GetWindow(this), this._preparationSettings, dialog.PreparationRepository, createdPath,
                    profile: dialog.PreparationProfile);
            }

            this.SetBusy(true);
            this._cherryState = dialog.Result;
            await this.LoadAsync(repository);
            this.OperationStatus.Text = dialog.Result?.Message ?? "";
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            this.OperationStatus.Text = exception.Message;
        }
        finally
        {
            this.SetBusy(false);
        }

        this.RepositoryStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void ContinueCherry_Click(object sender, RoutedEventArgs e) => await this.FinishCherryPickAsync(false);

    private async void AbortCherry_Click(object sender, RoutedEventArgs e) => await this.FinishCherryPickAsync(true);

    private async Task FinishCherryPickAsync(bool abort)
    {
        if (this._isBusy || this.SelectedRepository is not { } repository)
        {
            return;
        }

        if (abort && MessageBox.Show(Window.GetWindow(this), L10n.T("Отменить текущий cherry-pick и вернуть состояние до его начала?"), L10n.T("Отменить cherry-pick"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        this.SetBusy(true);
        this.OperationStatus.Text = L10n.T(abort ? "Отмена cherry-pick…" : "Продолжение cherry-pick…");

        try
        {
            var result = abort ? await this._operations.AbortCherryPickAsync(repository.Path) : await this._operations.ContinueCherryPickAsync(repository.Path);
            this._cherryState = result;
            await this.LoadAsync(repository);
            this.OperationStatus.Text = result.Message;
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            this.OperationStatus.Text = exception.Message;
            await this.RefreshCherryStateAsync(repository.Path);
        }
        finally
        {
            this.SetBusy(false);
        }

        this.RepositoryStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCherryState()
    {
        var inProgress = this._cherryState?.IsInProgress == true;
        this.ContinueCherryButton.Visibility = inProgress ? Visibility.Visible : Visibility.Collapsed;
        this.AbortCherryButton.Visibility = inProgress ? Visibility.Visible : Visibility.Collapsed;
        this.ContinueCherryButton.IsEnabled = inProgress && this._cherryState?.HasConflicts != true && this._cherryState?.IsEmpty != true;
        if (inProgress)
        {
            this.OperationStatus.Text = this._cherryState!.Message;
        }
    }

    private async Task RefreshCherryStateAsync(string repository)
    {
        try
        {
            this._cherryState = await this._operations.ReadCherryPickStateAsync(repository);
            this.UpdateCherryState();
        }
        catch (Exception exception) when (GitHistoryView.IsExpected(exception))
        {
            this.OperationStatus.Text += "\n" + L10n.T("Не удалось проверить cherry-pick: {0}", L10n.T(exception.Message));
        }
    }

    private void CopyCommit_Click(object sender, RoutedEventArgs e)
    {
        var menu = (sender as MenuItem)?.Parent as ContextMenu;
        var item = menu?.DataContext as GraphCommitItem ?? this.CommitsList.SelectedItem as GraphCommitItem;
        if (item is null)
        {
            return;
        }

        this.CommitsList.SelectedItem = item;
        try
        {
            Clipboard.SetText(item.Row.Commit.Hash);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            this.OperationStatus.Text = L10n.T("Буфер обмена занят. Повторите копирование.");
        }
    }

    private void FileHistory_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || this.SelectedRepository is not { } repository || this.CommitFiles.SelectedItem is not GraphChangedFile file)
        {
            return;
        }

        new FileHistoryWindow(repository.Path, file.File.Path, this._git) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
