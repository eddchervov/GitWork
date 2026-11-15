using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using GitWork.Core;

namespace GitWork.App;

public partial class CreateProjectWorkspaceWindow : Window
{
    private readonly IReadOnlyList<ProjectWorktreeChoice> _choices;
    private readonly string _parentFolder;
    private readonly string _projectName;
    private string? _suggestedPath;

    public CreateProjectWorkspaceWindow(ProjectGroup project, IReadOnlyList<ProjectWorktreeChoice> choices)
    {
        this._choices = choices;
        this._parentFolder = Path.Combine(Path.GetDirectoryName(choices[0].Repository.Path)!, "workspaces");
        this._projectName = CreateProjectWorkspaceWindow.GetFolderName(project.Name);
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.ProjectLabel.Text = L10n.T("{0} · repositories: {1}", project.Name, choices.Count);
        this.RepositoriesList.ItemsSource = choices;
        this.UpdateSuggestedPath();
    }

    public string WorkspaceName => this.WorkspaceNameBox.Text.Trim();

    private static string GetFolderName(string value) => new(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());

    private void WorkspaceNameBox_TextChanged(object sender, TextChangedEventArgs e) => this.UpdateSuggestedPath();

    private void UpdateSuggestedPath()
    {
        if (this.TargetFolderBox is null || (!string.IsNullOrEmpty(this.TargetFolderBox.Text) && this.TargetFolderBox.Text != this._suggestedPath))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(this.WorkspaceNameBox.Text) ? "new" : CreateProjectWorkspaceWindow.GetFolderName(this.WorkspaceNameBox.Text.Trim());
        this._suggestedPath = Path.Combine(this._parentFolder, $"{this._projectName}-{name}");
        this.TargetFolderBox.Text = this._suggestedPath;
    }

    public IReadOnlyList<WorktreePlan> Plans => this._choices.Select(choice => new WorktreePlan(
        choice.Repository.Path,
        Path.Combine(Path.GetFullPath(this.TargetFolderBox.Text.Trim()), choice.Repository.Name),
        this.BranchNameBox.Text.Trim(),
        choice.SelectedSource!.FullName)).ToArray();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L10n.T("Родительская папка комплекта") };

        if (dialog.ShowDialog(this) == true)
        {
            this.TargetFolderBox.Text = Path.Combine(dialog.FolderName, Path.GetFileName(this.TargetFolderBox.Text));
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(this.WorkspaceName) || string.IsNullOrWhiteSpace(this.BranchNameBox.Text)
            || this._choices.Any(choice => choice.SelectedSource is null))
        {
            this.ValidationText.Text = L10n.T("Укажите название, новую ветку и исходную ветку для каждого репозитория.");
            return;
        }

        if (!Path.IsPathFullyQualified(this.TargetFolderBox.Text.Trim()))
        {
            this.ValidationText.Text = L10n.T("Укажите полный путь общей папки.");
            return;
        }

        try
        {
            _ = this.Plans;
        }
        catch (ArgumentException)
        {
            this.ValidationText.Text = L10n.T("Путь содержит недопустимые символы.");
            return;
        }

        this.DialogResult = true;
    }
}
