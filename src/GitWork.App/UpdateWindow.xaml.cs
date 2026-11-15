using System.Diagnostics;
using System.Windows;

namespace GitWork.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly Func<bool> _mayExit;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private UpdateCheckResult? _result;

    public UpdateWindow(UpdateService updateService, Func<bool> mayExit, UpdateCheckResult? initialResult = null)
    {
        this._updateService = updateService;
        this._mayExit = mayExit;
        this._result = initialResult;
        this.InitializeComponent();
        this.Title = L10n.T("Обновления Git Work");
        this.CheckButton.Content = L10n.T("Проверить обновления");
        this.CloseButton.Content = L10n.T("Закрыть");
        this.VersionText.Text = L10n.T("Текущая версия: {0}", UpdateService.GetVersionForDisplay());
        if (initialResult is null)
        {
            this.StatusText.Text = L10n.T("Проверяем обновления…");
        }
        else
        {
            this.ShowResult(initialResult);
        }

        this.Loaded += this.UpdateWindow_Loaded;
        this.Closed += this.UpdateWindow_Closed;
    }

    public event Action<UpdateCheckResult>? CheckCompleted;

    private async void UpdateWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (this._result is null)
        {
            await this.CheckForUpdatesAsync();
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        await this.CheckForUpdatesAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (this._result is null)
        {
            return;
        }

        if (this._result.UpdateInfo is null)
        {
            this.OpenInstaller();
            return;
        }

        this.UpdateButton.IsEnabled = false;
        this.CheckButton.IsEnabled = false;
        this.DownloadProgress.Visibility = Visibility.Visible;
        this.StatusText.Text = L10n.T("Подготовка обновления…");
        var progress = new Progress<int>(percentage =>
        {
            this.DownloadProgress.Value = percentage;
            this.StatusText.Text = L10n.T("Загрузка обновления: {0}%", percentage);
        });

        try
        {
            await this._updateService.DownloadAndApplyAsync(
                this._result,
                this._mayExit,
                progress,
                this._cancellationTokenSource.Token);

            if (!this._mayExit())
            {
                this.StatusText.Text = L10n.T("Сначала завершите текущие операции и сохраните работу.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            this.StatusText.Text = L10n.T("Загрузка обновления отменена.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            this.StatusText.Text = L10n.T("Не удалось загрузить обновление. Повторите попытку позже.");
        }
        finally
        {
            this.UpdateButton.IsEnabled = true;
            this.CheckButton.IsEnabled = true;
            this.DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        this.CheckButton.IsEnabled = false;
        this.UpdateButton.Visibility = Visibility.Collapsed;
        this.DetailsText.Text = string.Empty;
        this.StatusText.Text = L10n.T("Проверяем обновления…");

        try
        {
            this._result = await this._updateService.CheckAsync(this._cancellationTokenSource.Token);
            this.ShowResult(this._result);
            this.CheckCompleted?.Invoke(this._result);
        }
        catch (OperationCanceledException)
        {
            this.StatusText.Text = L10n.T("Проверка отменена.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            this.StatusText.Text = L10n.T("Не удалось проверить обновления. Проверьте подключение и повторите попытку.");
        }
        finally
        {
            this.CheckButton.IsEnabled = true;
        }
    }

    private void ShowResult(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable:
                this.StatusText.Text = L10n.T("Доступна версия {0}", result.LatestVersion ?? string.Empty);
                if (result.UpdateInfo is not null)
                {
                    this.UpdateButton.Content = L10n.T("Установить и перезапустить");
                    this.UpdateButton.Visibility = Visibility.Visible;
                }
                else if (result.InstallerUrl is not null)
                {
                    this.UpdateButton.Content = L10n.T("Открыть установщик на GitHub");
                    this.UpdateButton.Visibility = Visibility.Visible;
                    this.DetailsText.Text = L10n.T("Эта копия не установлена через Velopack. Установщик откроется на странице GitHub; обновление внутри этой папки недоступно.");
                }
                else
                {
                    this.StatusText.Text = L10n.T("Доступна версия {0}, но установщик в релизе пока не найден.", result.LatestVersion ?? string.Empty);
                }

                this.AppendReleaseNotes(result.ReleaseNotes);
                break;
            case UpdateCheckStatus.UpToDate:
                this.StatusText.Text = L10n.T("У вас актуальная версия Git Work.");
                if (!result.IsInstalled && result.InstallerUrl is not null)
                {
                    this.UpdateButton.Content = L10n.T("Установить эту версию через Velopack");
                    this.UpdateButton.Visibility = Visibility.Visible;
                    this.DetailsText.Text = L10n.T("Эта копия не установлена через Velopack. Установщик откроется на странице GitHub; обновление внутри этой папки недоступно.");
                }

                this.AppendReleaseNotes(result.ReleaseNotes);
                break;
            case UpdateCheckStatus.NotPublished:
                this.StatusText.Text = L10n.T("Обновления ещё не опубликованы.");
                break;
            case UpdateCheckStatus.NoReleaseSource:
                this.StatusText.Text = L10n.T("Источник обновлений пока не найден.");
                break;
            default:
                this.StatusText.Text = L10n.T("Не удалось проверить обновления. Проверьте подключение и повторите попытку.");
                break;
        }
    }

    private void AppendReleaseNotes(string? releaseNotes)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes))
        {
            return;
        }

        var notes = $"{L10n.T("Заметки к выпуску")}:\n\n{releaseNotes.Trim()}";
        this.DetailsText.Text = string.IsNullOrWhiteSpace(this.DetailsText.Text)
            ? notes
            : this.DetailsText.Text + "\n\n" + notes;
    }

    private void OpenInstaller()
    {
        if (this._result?.InstallerUrl is not { } installerUrl)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(installerUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine(exception);
            this.StatusText.Text = L10n.T("Не удалось открыть ссылку на установщик.");
        }
    }

    private void UpdateWindow_Closed(object? sender, EventArgs e)
    {
        this._cancellationTokenSource.Cancel();
        this._cancellationTokenSource.Dispose();
    }
}
