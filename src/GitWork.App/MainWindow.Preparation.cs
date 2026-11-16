using System.Windows;

namespace GitWork.App;

public partial class MainWindow
{
    private void PreparationProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        var dialog = new PreparationProfilesWindow(this._settings.PreparationProfiles) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            this._settings.PreparationProfiles = dialog.Profiles.ToList();
            this._settings.PreparationBindings.RemoveAll(binding => !this._settings.PreparationProfiles.Any(profile => profile.Id == binding.ProfileId));
            this.SaveSettings();
        }
    }

    private async void RunPreparation_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            return;
        }

        var workspace = this._activeWorkspace;
        var root = workspace?.RootPath ?? this._worktree?.Path;
        var repository = workspace?.PrimaryRepositoryPath ?? workspace?.RepositoryPaths.FirstOrDefault() ?? this._repository?.Path;
        if (root is null || repository is null || workspace is null && this._worktree is not { IsMain: false, IsBare: false })
        {
            MessageBox.Show(this, L10n.T("Выберите worktree или комплект для подготовки."), L10n.T("Подготовка"));
            return;
        }

        if (PreparationLauncher.Show(this, this._settings, repository, root, workspace is not null, choose: true))
        {
            await this.ExecuteAsync(this.ScanAsync, L10n.T("Обновление рабочих копий…"));
        }
    }
}
