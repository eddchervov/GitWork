using System.Windows;
using System.Windows.Controls;

namespace GitWork.App;

public partial class MainWindow
{
    private void UpdateLanguageMenu()
    {
        this.RussianLanguageItem.IsChecked = L10n.Language == "ru";
        this.EnglishLanguageItem.IsChecked = L10n.Language == "en";
    }

    private async void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string language } || this._isBusy || !this.CanLeaveGitClients)
        {
            this.UpdateLanguageMenu();
            return;
        }

        L10n.SetLanguage(language);
        this._settings.Language = L10n.Language;
        this.UpdateLanguageMenu();
        this.SaveSettings();
        await this.ExecuteAsync(
            () => this.ActiveGitClient is { } client ? client.RefreshAsync() : this.ScanAsync(),
            L10n.T("Обновление языка…"));
    }
}
