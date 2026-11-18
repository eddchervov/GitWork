using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class ChangesView
{
    private async void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        if (this.IsBusy || this.RepositoryPicker.SelectedItem is not ChangeSource source)
        {
            return;
        }

        this.SetBusy(true);
        var status = "";
        try
        {
            var service = new GitFileActionsService(this._git);
            var preview = await service.GetDiscardAllPreviewAsync(source.Path);
            var message = L10n.T("Отменить все изменения в «{0}»? Файлов: {1}. Включая подготовленные, новые и скрытые поиском файлы.",
                source.Name, preview.Files.Count)
                + "\n" + source.Path + "\n\n"
                + L10n.T("Рабочие и подготовленные версии будут сохранены в резервную копию. Вложенные репозитории, worktree и игнорируемые файлы не затрагиваются.");

            if (MessageBox.Show(Window.GetWindow(this), message, L10n.T("Отменить все изменения"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            var backup = await service.DiscardAllAsync(preview);
            status = backup is null ? L10n.T("Изменения файлов отменены.")
                : L10n.T("Изменения отменены. Резервная копия: {0}", backup);
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            status = L10n.T(exception.Message);
        }
        finally
        {
            try
            {
                await this.ReloadSourcesAsync();
            }
            catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException)
            {
                status += "\n" + L10n.T("Не удалось обновить список: {0}", L10n.T(exception.Message));
            }
            finally
            {
                this.SetBusy(false);
                if (status.Length > 0 && Window.GetWindow(this)?.FindName("StatusText") is TextBlock statusText)
                {
                    statusText.Text = status;
                    statusText.ToolTip = status;
                }
            }
        }
    }
}
