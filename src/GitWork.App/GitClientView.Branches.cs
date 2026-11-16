using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView
{
    private IReadOnlyList<BranchInfo> _branches = [];

    private void InitializeBranchPicker()
    {
        this.BranchPicker.CloseRequested += (_, _) => this.CloseBranchPicker();
        this.BranchPicker.CreateBranchRequested += (_, _) => this.NewBranch_Click(this, new RoutedEventArgs());
        this.BranchPicker.BranchSelected += async branch => await this.SelectBranchAsync(branch);
        this.IsVisibleChanged += (_, _) =>
        {
            if (!this.IsVisible)
            {
                this.CloseBranchPicker();
            }
        };
    }

    private async void Branches_Click(object sender, RoutedEventArgs e)
    {
        if (this.BranchPickerHost.Visibility == Visibility.Visible)
        {
            this.CloseBranchPicker();
            return;
        }

        if (!this.CanLeave || this._selectedCopy is not { } tree)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            this._repositoryStatusVersion++;
            var trees = await this._git.GetWorktreesAsync(tree.Path);
            var current = trees.FirstOrDefault(copy => GitService.PathsEqual(copy.Path, tree.Path) && !copy.IsPrunable && !copy.IsBare)
                ?? throw new GitException("Рабочая копия недоступна.");
            var branches = await this._git.GetBranchesAsync(tree.Path, trees);
            var files = await this._git.GetStatusAsync(tree.Path, true);
            this._branches = branches;
            this._selectedCopy = current;
            this.CurrentBranchLabel.Text = current.BranchLabel;
            this.GetRepositoryState(tree.Path).Update(current, files.Count);
            this.BranchPicker.SetBranches(branches, current, files.Count > 0);
            this.BranchPickerHost.Visibility = Visibility.Visible;
            this.UpdateLayout();
            this.PositionBranchPicker();
            this.BranchPicker.FocusSearch();
        });
    }

    private async Task SelectBranchAsync(BranchInfo branch)
    {
        if (!this.CanLeave || this._selectedCopy is not { } tree)
        {
            return;
        }

        this.CloseBranchPicker();
        if (branch.IsRemote)
        {
            var dialog = new CreateBranchWindow(tree.Path, this._branches, tree.Branch, branch.FullName) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await this.RunAsync(async () =>
            {
                await this._git.CreateBranchAsync(tree.Path, dialog.BranchName, dialog.StartPoint);
                this._repositoryStatusVersion++;
                await this.ShowCurrentViewAsync();
            });
            return;
        }

        if (branch.Name == tree.Branch || branch.OccupiedPath is not null && !GitService.PathsEqual(branch.OccupiedPath, tree.Path))
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            await this._git.SwitchBranchAsync(tree.Path, branch.Name);
            this._repositoryStatusVersion++;
            await this.ShowCurrentViewAsync();
        });
    }

    private void PositionBranchPicker()
    {
        if (this.BranchPickerHost.Visibility != Visibility.Visible || this.ClientRoot.ActualWidth <= 0)
        {
            return;
        }

        var anchor = this.BranchSelectorButton.TranslatePoint(new Point(0, this.BranchSelectorButton.ActualHeight + 5), this.ClientRoot);
        var width = Math.Min(570, Math.Max(0, this.ClientRoot.ActualWidth - 24));
        var left = Math.Max(12, Math.Min(anchor.X, this.ClientRoot.ActualWidth - width - 12));
        var top = Math.Max(0, anchor.Y);
        this.BranchPickerHost.Width = width;
        this.BranchPickerHost.Height = Math.Max(0, this.ClientRoot.ActualHeight - top - 12);
        this.BranchPickerHost.Margin = new Thickness(left, top, 0, 0);
    }

    private void CloseBranchPicker()
    {
        if (this.BranchPickerHost is not null)
        {
            this.BranchPickerHost.Visibility = Visibility.Collapsed;
        }
    }

    private void Client_SizeChanged(object sender, SizeChangedEventArgs e) => this.PositionBranchPicker();

    private void Client_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && this.BranchPickerHost.Visibility == Visibility.Visible)
        {
            this.CloseBranchPicker();
            this.BranchSelectorButton.Focus();
            e.Handled = true;
        }
    }

    private void Client_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (this.BranchPickerHost.Visibility != Visibility.Visible)
        {
            return;
        }

        var element = e.OriginalSource as DependencyObject;
        while (element is not null)
        {
            if (element == this.BranchPickerHost || element == this.BranchSelectorButton)
            {
                return;
            }

            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        this.CloseBranchPicker();
    }
}
