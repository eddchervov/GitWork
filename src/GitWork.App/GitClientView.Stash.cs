using System.Windows;

namespace GitWork.App;

public partial class GitClientView
{
    private async void Stash_Click(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || this._selectedCopy is not { IsBare: false, IsPrunable: false } tree)
        {
            return;
        }

        var window = new StashWindow(this._git, tree.Path) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        if (!window.WorkingTreeChanged)
        {
            return;
        }

        await this.RunAsync(async () =>
        {
            await this.ShowCurrentViewAsync();
            await this.RefreshRepositoryStatusesAsync();
        });
    }
}
