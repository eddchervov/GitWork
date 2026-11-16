using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class CreateBranchWindow : Window
{
    private readonly IReadOnlyList<BranchInfo> _branches;

    public CreateBranchWindow(string worktree, IReadOnlyList<BranchInfo> branches, string? currentBranch, string? sourceRef = null)
    {
        this._branches = branches;
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.WorktreeLabel.Text = worktree;

        var startPoints = new List<BranchStartPoint> { new("HEAD", L10n.T("HEAD · текущий коммит")) };
        startPoints.AddRange(branches.Select(branch => new BranchStartPoint(
            branch.FullName,
            L10n.T("{0} · {1}", branch.Name, L10n.T(branch.IsRemote ? "удалённая" : "локальная")))));
        this.StartPointPicker.ItemsSource = startPoints;
        this.StartPointPicker.SelectedItem = startPoints.FirstOrDefault(point => point.Ref == (sourceRef ?? "refs/heads/" + currentBranch))
            ?? startPoints[0];
    }

    public string BranchName => this.BranchNameBox.Text.Trim();

    public string StartPoint => ((BranchStartPoint)this.StartPointPicker.SelectedItem).Ref;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        this.ValidationText.Text = "";
        var branch = this.BranchName;

        if (string.IsNullOrWhiteSpace(branch))
        {
            this.ValidationText.Text = L10n.T("Введите имя новой ветки.");
            return;
        }

        if (this._branches.Any(existing => !existing.IsRemote && existing.Name == branch))
        {
            this.ValidationText.Text = L10n.T("Локальная ветка с таким именем уже существует.");
            return;
        }

        if (this.StartPointPicker.SelectedItem is not BranchStartPoint)
        {
            this.ValidationText.Text = L10n.T("Выберите исходную ветку.");
            return;
        }

        this.DialogResult = true;
    }
}

public sealed record BranchStartPoint(string Ref, string DisplayName)
{
    public override string ToString() => this.DisplayName;
}
