using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GitWork.Core;

namespace GitWork.App;

public partial class BranchPickerView : UserControl
{
    private IReadOnlyList<BranchPickerChoice> _choices = [];
    private IReadOnlyList<BranchPickerChoice> _visibleChoices = [];
    private bool _hasChanges;
    private bool _updating;

    public event Action<BranchInfo>? BranchSelected;

    public event EventHandler? CreateBranchRequested;

    public event EventHandler? CloseRequested;

    public BranchPickerView()
    {
        this.InitializeComponent();
    }

    public void SetBranches(IReadOnlyList<BranchInfo> branches, WorktreeInfo tree, bool hasChanges)
    {
        this._updating = true;
        this._hasChanges = hasChanges;
        this.CurrentBranchLabel.Text = tree.BranchLabel;
        this.DirtyWarning.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        this.BranchesList.IsEnabled = !hasChanges;
        this.NewBranchButton.IsEnabled = !hasChanges;

        var choices = branches
            .Where(branch => branch.IsRemote || !string.Equals(branch.Name, tree.Branch, StringComparison.Ordinal))
            .Select(branch => new BranchPickerChoice(
                branch,
                L10n.T(branch.IsRemote ? "УДАЛЁННЫЕ ВЕТКИ" : "ЛОКАЛЬНЫЕ ВЕТКИ"),
                branch.OccupiedPath is null || GitService.PathsEqual(branch.OccupiedPath, tree.Path),
                branch.IsRemote
                    ? L10n.T("Создать локальную ветку…")
                    : branch.OccupiedPath is not null && !GitService.PathsEqual(branch.OccupiedPath, tree.Path)
                        ? L10n.T("Занята в другом worktree")
                        : ""))
            .ToArray();

        this._choices = choices;
        this.SearchBox.Text = "";
        this.ApplyFilter();
        this._updating = false;
    }

    public void FocusSearch() => this.SearchBox.Focus();

    private void ApplyFilter()
    {
        this._visibleChoices = this._choices
            .Where(choice => SearchMatcher.Matches(this.SearchBox.Text, choice.Branch.Name, choice.Branch.FullName))
            .ToArray();

        var view = new ListCollectionView(this._visibleChoices.ToArray());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BranchPickerChoice.GroupName)));
        this.BranchesList.ItemsSource = view;
        this.BranchesList.SelectedItem = null;
        this.EmptyState.Visibility = this._visibleChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (this._updating || this.BranchesList is null)
        {
            return;
        }

        this.ApplyFilter();
    }

    private void BranchesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (this._hasChanges || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(this.BranchesList, source) is not ListBoxItem { DataContext: BranchPickerChoice choice })
        {
            return;
        }

        this.ActivateChoice(choice);
        e.Handled = true;
    }

    private void Picker_Loaded(object sender, RoutedEventArgs e)
    {
        this.SearchBox.Focus();
    }

    private void Picker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource == this.SearchBox)
        {
            if (e.Key == Key.Down)
            {
                this.FocusFirstAvailableBranch();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                var availableChoices = this._visibleChoices.Where(choice => choice.IsSelectable).Take(2).ToArray();
                if (!this._hasChanges && availableChoices.Length == 1)
                {
                    this.ActivateChoice(availableChoices[0]);
                }

                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            this.CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || this.BranchesList.SelectedItem is not BranchPickerChoice choice)
        {
            return;
        }

        this.ActivateChoice(choice);
        e.Handled = true;
    }

    private void FocusFirstAvailableBranch()
    {
        var firstAvailable = this._visibleChoices.FirstOrDefault(choice => choice.IsSelectable);
        this.BranchesList.Focus();
        if (firstAvailable is not null)
        {
            this.BranchesList.SelectedItem = firstAvailable;
            this.BranchesList.ScrollIntoView(firstAvailable);
        }
    }

    private void ActivateChoice(BranchPickerChoice choice)
    {
        if (!this._hasChanges && choice.IsSelectable)
        {
            this.BranchSelected?.Invoke(choice.Branch);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => this.CloseRequested?.Invoke(this, EventArgs.Empty);

    private void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (!this._hasChanges)
        {
            this.CreateBranchRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
