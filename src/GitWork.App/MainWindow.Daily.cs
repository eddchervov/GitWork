using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class MainWindow
{
    private async void FileHistory_Click(object sender, RoutedEventArgs e)
    {
        if (this._worktree is not { } tree || this._isBusy)
        {
            return;
        }

        await this.ExecuteAsync(async () =>
        {
            var file = this.FilesList.SelectedItem as ChangedFile;
            var path = file?.OriginalPath ?? file?.Path;
            var currentPath = file?.Path;

            if (path is null)
            {
                var output = await this._git.RunAsync(tree.Path, "ls-files", "-z");
                var files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var picker = new SelectTrackedFileWindow(files) { Owner = this };

                if (picker.ShowDialog() != true || picker.SelectedPath is null)
                {
                    return;
                }

                path = picker.SelectedPath;
            }

            new FileHistoryWindow(tree.Path, path, this._git, currentPath) { Owner = this }.ShowDialog();
            this.StatusText.Text = L10n.T("Готово");
        }, L10n.T("Чтение истории файла…"));
    }
}
