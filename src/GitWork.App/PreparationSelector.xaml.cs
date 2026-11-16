using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class PreparationSelector : UserControl
{
    private AppSettings? _settings;
    private string _repositoryPath = "";

    public PreparationSelector() => this.InitializeComponent();

    public PreparationProfile? SelectedProfile => this.ProfilePicker.SelectedItem as PreparationProfile;

    public PreparationProfile? ProfileToRun => this.RunCheck.IsChecked == true ? this.SelectedProfile : null;

    public void Configure(AppSettings settings, string repositoryPath, bool bundle = false, string? worktreePath = null)
    {
        this._settings = settings;
        this._repositoryPath = repositoryPath;
        var binding = PreparationPreferences.Find(settings, repositoryPath, worktreePath);
        this.GroupCheck.Content = L10n.T("Использовать по умолчанию для группы «{0}»", Path.GetFileName(Path.GetDirectoryName(repositoryPath)));
        this.GroupCheck.IsChecked = binding?.Scope == "Group";
        this.DirectoryHint.Text = L10n.T(bundle
            ? "Рабочая папка — корень нового комплекта. Запуск после создания всех worktree."
            : "Рабочая папка — новая папка worktree.");
        this.RefreshProfiles(binding?.ProfileId);
        this.RunCheck.IsChecked = binding?.RunAutomatically == true;
    }

    public bool RememberSelection(string? worktreePath = null)
    {
        this.ValidationText.Text = "";
        if (this.RunCheck.IsChecked == true && this.SelectedProfile is null)
        {
            this.ValidationText.Text = L10n.T("Выберите или создайте профиль подготовки.");

            return false;
        }

        if (worktreePath is null && this.RunCheck.IsChecked != true)
        {
            return true;
        }

        if (this._settings is not null && this.SelectedProfile is { } profile)
        {
            PreparationPreferences.Remember(this._settings, this._repositoryPath, profile.Id,
                this.RunCheck.IsChecked == true, this.GroupCheck.IsChecked == true, worktreePath);
        }

        return true;
    }

    private void RefreshProfiles(string? selectedId)
    {
        this.ProfilePicker.ItemsSource = this._settings?.PreparationProfiles.ToArray();
        this.ProfilePicker.SelectedItem = this._settings?.PreparationProfiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? this._settings?.PreparationProfiles.FirstOrDefault();
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (this.StepsPreview is not null)
        {
            this.StepsPreview.ItemsSource = this.SelectedProfile?.Steps.Where(step => step.IsEnabled)
                .Select((step, index) => $"{index + 1}. {step.Name}").ToArray();
        }
    }

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        if (this._settings is null)
        {
            return;
        }

        var dialog = new PreparationProfilesWindow(this._settings.PreparationProfiles, this.SelectedProfile?.Id) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        this._settings.PreparationProfiles = dialog.Profiles.ToList();
        this._settings.PreparationBindings.RemoveAll(binding => !this._settings.PreparationProfiles.Any(profile => profile.Id == binding.ProfileId));
        this._settings.Save();
        this.RefreshProfiles(dialog.SelectedProfileId);
        this.RunCheck.IsChecked = this.SelectedProfile is not null;
    }
}
