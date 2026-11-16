using System.Windows;
using System.Windows.Controls;

namespace GitWork.App;

public partial class GitClientView
{
    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            this.PrepareMenuItem.IsEnabled = this.CanLeave && this._selectedCopy is { IsMain: false, IsBare: false, IsPrunable: false };
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void Preparation_Click(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || this._selectedCopy is not { IsMain: false, IsBare: false, IsPrunable: false } tree
            || this._selectedRepository is not { } repository)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            if (PreparationLauncher.Show(Window.GetWindow(this), this._settings, repository.Path, tree.Path, choose: true))
            {
                await this.ShowCurrentViewAsync();
                await this.RefreshRepositoryStatusesAsync();
            }
        });
    }
}
