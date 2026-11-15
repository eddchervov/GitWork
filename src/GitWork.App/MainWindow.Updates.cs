using System.Windows;
using System.Windows.Threading;

namespace GitWork.App;

public partial class MainWindow
{
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    private UpdateCheckResult? _lastUpdateResult;
    private bool _checkingUpdates;

    private async void InitializeUpdateChecks(object sender, RoutedEventArgs e)
    {
        this._updateTimer.Tick += async (_, _) => await this.CheckUpdatesInBackgroundAsync();
        this.Closed += (_, _) => this._updateTimer.Stop();
        this._updateTimer.Start();
        await this.CheckUpdatesInBackgroundAsync();
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        if (this._isClosed || this._checkingUpdates || this._settings.LastUpdateCheckUtc is { } previous
            && DateTimeOffset.UtcNow - previous < TimeSpan.FromDays(1))
        {
            return;
        }

        this._checkingUpdates = true;
        this._settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        this.SaveSettings();
        try
        {
            var result = await this._updates.CheckAsync();
            if (!this._isClosed)
            {
                this.RecordUpdateResult(result, false);
            }
        }
        finally
        {
            this._checkingUpdates = false;
        }
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (this._isBusy || !this.CanLeaveGitClients)
        {
            this.StatusText.Text = L10n.T("Завершите текущую операцию перед обновлением.");
            return;
        }

        var initialResult = ReferenceEquals(sender, this.UpdatesButton) && this._lastUpdateResult?.HasUpdate == true
            ? this._lastUpdateResult : null;
        var dialog = new UpdateWindow(this._updates, () => !this._isBusy && this.CanLeaveGitClients, initialResult) { Owner = this };
        dialog.CheckCompleted += result => this.RecordUpdateResult(result, true);
        if (initialResult?.HasUpdate == true)
        {
            this.RecordUpdateResult(initialResult, true);
        }

        dialog.ShowDialog();
    }

    private void RecordUpdateResult(UpdateCheckResult result, bool seen)
    {
        this._lastUpdateResult = result;
        this._settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        if (seen && result.HasUpdate)
        {
            this._settings.LastSeenUpdateVersion = result.LatestVersion;
        }

        this.UpdateBadge.Visibility = result.HasUpdate && this._settings.LastSeenUpdateVersion != result.LatestVersion
            ? Visibility.Visible : Visibility.Collapsed;
        this.UpdatesButton.ToolTip = result.HasUpdate
            ? L10n.T("Доступна новая версия: {0}", result.LatestVersion ?? "")
            : L10n.T("Проверить обновления");
        this.SaveSettings();
    }
}
