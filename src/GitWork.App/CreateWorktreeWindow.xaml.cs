using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using GitWork.Core;

namespace GitWork.App;

public partial class CreateWorktreeWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly IReadOnlyList<BranchInfo> _branches;
    private string? _suggestedPath;

    public CreateWorktreeWindow(RepositoryInfo repository, IReadOnlyList<BranchInfo> branches, string? currentBranch)
    {
        this._repository = repository;
        this._branches = branches;
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.RepositoryLabel.Text = repository.Path;
        this.BasePicker.ItemsSource = branches.Select(branch => branch with { OccupiedPath = null }).ToArray();
        this.BasePicker.SelectedItem = this.BasePicker.Items.OfType<BranchInfo>().FirstOrDefault(branch => branch.Name == currentBranch)
            ?? this.BasePicker.Items.OfType<BranchInfo>().FirstOrDefault();
        this.ExistingPicker.ItemsSource = branches.Where(branch => !branch.IsRemote).ToArray();
        this.ExistingPicker.SelectedItem = branches.FirstOrDefault(branch => !branch.IsRemote && branch.IsAvailable);
        this.SuggestPath();
    }

    public string TargetPath => Path.GetFullPath(this.TargetPathBox.Text.Trim());

    public string BranchName => this.ModePicker.SelectedIndex == 0
        ? this.BranchNameBox.Text.Trim()
        : ((BranchInfo)this.ExistingPicker.SelectedItem).Name;

    public string? StartPoint => this.ModePicker.SelectedIndex == 0
        ? ((BranchInfo)this.BasePicker.SelectedItem).FullName
        : null;

    private void ModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.NewBranchPanel is null)
        {
            return;
        }

        this.NewBranchPanel.Visibility = this.ModePicker.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.ExistingBranchPanel.Visibility = this.ModePicker.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        this.SuggestPath();
    }

    private void BranchNameBox_TextChanged(object sender, TextChangedEventArgs e) => this.SuggestPath();

    private void ExistingPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => this.SuggestPath();

    private void SuggestPath()
    {
        if (this.TargetPathBox is null || this._repository is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(this.TargetPathBox.Text) && this.TargetPathBox.Text != this._suggestedPath)
        {
            return;
        }

        var name = this.ModePicker.SelectedIndex == 0 ? this.BranchNameBox.Text : (this.ExistingPicker.SelectedItem as BranchInfo)?.Name ?? "new";
        var safeName = new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || character == '/' ? '-' : character).ToArray()).Trim('-');
        var parent = Path.GetDirectoryName(this._repository.Path)!;
        this._suggestedPath = Path.Combine(parent, $"{this._repository.Name}-{safeName}");
        this.TargetPathBox.Text = this._suggestedPath;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L10n.T("Родительская папка для нового worktree"), InitialDirectory = Path.GetDirectoryName(this._repository.Path)! };

        if (dialog.ShowDialog(this) == true)
        {
            this.TargetPathBox.Text = Path.Combine(dialog.FolderName, Path.GetFileName(this.TargetPathBox.Text));
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        this.ValidationText.Text = "";

        if (this.ModePicker.SelectedIndex == 0)
        {
            if (string.IsNullOrWhiteSpace(this.BranchNameBox.Text) || this.BasePicker.SelectedItem is not BranchInfo)
            {
                this.ValidationText.Text = L10n.T("Введите имя новой ветки и выберите исходную ветку.");
                return;
            }

            if (this._branches.Any(branch => !branch.IsRemote && branch.Name == this.BranchNameBox.Text.Trim()))
            {
                this.ValidationText.Text = L10n.T("Такая ветка уже существует. Выберите режим существующей ветки.");
                return;
            }
        }
        else if (this.ExistingPicker.SelectedItem is not BranchInfo { IsAvailable: true })
        {
            this.ValidationText.Text = L10n.T("Выберите свободную локальную ветку.");
            return;
        }

        if (!Path.IsPathFullyQualified(this.TargetPathBox.Text.Trim()))
        {
            this.ValidationText.Text = L10n.T("Укажите полный путь новой папки.");
            return;
        }

        try
        {
            if (Directory.Exists(this.TargetPath) || File.Exists(this.TargetPath))
            {
                this.ValidationText.Text = L10n.T("Папка уже существует. Выберите новое имя.");
                return;
            }
        }
        catch (ArgumentException)
        {
            this.ValidationText.Text = L10n.T("Путь содержит недопустимые символы.");
            return;
        }

        this.DialogResult = true;
    }
}
