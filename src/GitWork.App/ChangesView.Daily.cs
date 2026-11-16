using System.IO;
using System.Windows;
using System.Windows.Threading;
using GitWork.Core;

namespace GitWork.App;

public partial class ChangesView
{
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _liveRefreshing;
    private readonly bool _autoRefresh;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        this.Loaded += (_, _) => this._liveTimer.Start();
        this.Unloaded += (_, _) => this._liveTimer.Stop();
        this._liveTimer.Tick += async (_, _) =>
        {
            if (!this.IsVisible || Window.GetWindow(this)?.IsActive != true || this.IsBusy || this._liveRefreshing || !this._autoRefresh)
            {
                return;
            }

            this._liveRefreshing = true;
            var offset = ViewState.ScrollOffset(this.FilesList);
            try
            {
                await this.RefreshAsync();
                ViewState.RestoreScroll(this.FilesList, offset);
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                this.ShowCommitStatus(L10n.T("Обновление недоступно: {0}", L10n.T(exception.Message)));
            }
            finally
            {
                this._liveRefreshing = false;
            }
        };
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (this.IsBusy || this.SelectedSource is not { } source || this.SelectedFile is not { IsDirectory: false } file)
        {
            return;
        }

        new FileHistoryWindow(source.Path, file.OriginalPath ?? file.Path, this._git, file.Path) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
