using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class WorkspaceBranchWindow : Window
{
    private readonly IReadOnlyList<WorkspaceBranchChoice> _choices;

    public WorkspaceBranchWindow(string name, IReadOnlyList<WorkspaceBranchChoice> choices)
    {
        this._choices = choices;
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.WorkspaceLabel.Text = name;
        this.ChoicesList.ItemsSource = choices;
    }

    public IReadOnlyList<WorkspaceBranchPlan> Plans { get; private set; } = [];

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var included = this._choices.Where(choice => choice.IsIncluded && choice.IsAvailable).ToArray();
        var name = this.BranchNameBox.Text.Trim();

        if (included.Length == 0 || string.IsNullOrWhiteSpace(name))
        {
            this.ValidationText.Text = L10n.T("Введите имя ветки и выберите репозитории");
            return;
        }

        this.Plans = included.Select(choice => new WorkspaceBranchPlan(choice.RepositoryPath, choice.Path, name, choice.SelectedSource.Ref)).ToArray();
        this.DialogResult = true;
    }
}

public sealed class WorkspaceBranchChoice
{
    public required string RepositoryPath { get; init; }

    public required string Path { get; init; }

    public required string Name { get; init; }

    public required string BranchLabel { get; init; }

    public string? Error { get; init; }

    public bool IsIncluded { get; set; } = true;

    public bool IsAvailable => this.Error is null;

    public IReadOnlyList<BranchStartPoint> Sources { get; init; } = [];

    public BranchStartPoint SelectedSource { get; set; } = new("HEAD", L10n.T("Текущий коммит (HEAD)"));
}
