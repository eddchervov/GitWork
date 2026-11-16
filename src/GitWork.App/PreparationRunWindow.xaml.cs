using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using GitWork.Core;
using Microsoft.Win32;

namespace GitWork.App;

public partial class PreparationRunWindow : Window
{
    private readonly PreparationProfile _profile;
    private readonly string _root;
    private readonly PreparationRunRow[] _rows;
    private CancellationTokenSource? _cancellation;
    private int _retryIndex;
    private int _runVersion;
    private bool _isRunning;
    private bool _closeRequested;

    public PreparationRunWindow(PreparationProfile profile, string rootPath)
    {
        this.InitializeComponent();
        this._profile = JsonSerializer.Deserialize<PreparationProfile>(JsonSerializer.Serialize(profile))!;
        this._root = Path.GetFullPath(rootPath);
        this._rows = this._profile.Steps.Select((step, index) => new PreparationRunRow(index + 1, step.Name)).ToArray();
        this.StepsList.ItemsSource = this._rows;
        this.ProfileTitle.Text = profile.Name;
        this.RootPathText.Text = this._root;
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
    }

    public bool IsRunning => this._isRunning;

    public PreparationRunResult? Result { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await this.RunAsync();

    private async Task RunAsync()
    {
        if (this._isRunning)
        {
            return;
        }

        this._isRunning = true;
        var version = ++this._runVersion;
        this.RetryButton.Visibility = Visibility.Collapsed;
        this.Progress.Visibility = Visibility.Visible;
        this.CloseButton.Content = L10n.T("Остановить");
        this.StatusText.Text = L10n.T("Подготовка выполняется…");
        this._cancellation = new CancellationTokenSource();
        try
        {
            var profile = JsonSerializer.Deserialize<PreparationProfile>(JsonSerializer.Serialize(this._profile))!;
            for (var index = this._retryIndex; index < profile.Steps.Count; index++)
            {
                if (this._rows[index].Succeeded)
                {
                    profile.Steps[index].IsEnabled = false;
                }
            }

            var environment = PreparationPreferences.GetEnvironment(profile);
            var progress = new Progress<PreparationOutput>(output =>
            {
                if (version == this._runVersion)
                {
                    this.Report(output);
                }
            });
            this.Result = await new PreparationRunner().RunAsync(profile, this._root, environment, progress, this._retryIndex, this._cancellation.Token);
            this._retryIndex = this.Result.FailedStepIndex
                ?? this.Result.Steps.FirstOrDefault(step => step.IsCanceled)?.StepIndex
                ?? this._rows.ToList().FindIndex(row => !row.Succeeded);
            this._retryIndex = Math.Max(0, this._retryIndex);
            this.StatusText.Text = L10n.T(this.Result.Success ? "Подготовка завершена." : this.Result.IsCanceled ? "Подготовка остановлена." : "Подготовка завершилась с ошибкой.");
            this.RetryButton.Visibility = this.Result.Success ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or CryptographicException or FormatException)
        {
            this.StatusText.Text = L10n.T("Не удалось запустить подготовку. Проверьте профиль и значения переменных.");
            this.RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            this._cancellation.Dispose();
            this._cancellation = null;
            this._isRunning = false;
            this.CloseButton.IsEnabled = true;
            this.CloseButton.Content = L10n.T("Закрыть");
            this.Progress.Visibility = Visibility.Collapsed;
            if (this._closeRequested)
            {
                this.Close();
            }
        }
    }

    private void Report(PreparationOutput output)
    {
        var row = this._rows[output.StepIndex];
        if (output.IsStarting)
        {
            row.Start((Brush)this.FindResource("AccentBrush"));
            this.AppendOutput($"\r\n[{output.StepIndex + 1}] {row.Name}\r\n");
        }

        if (output.Result is { } result && !(result.IsSkipped && row.Succeeded))
        {
            var status = result.IsSkipped ? L10n.T("Пропущен") : result.IsCanceled ? L10n.T("Остановлен")
                : result.ExitCode == 0 ? L10n.T("Готово") : L10n.T("Ошибка · код {0}", result.ExitCode);
            row.Finish(status, (Brush)this.FindResource(result.ExitCode == 0 ? "AddedTextBrush" : result.IsSkipped ? "MutedBrush" : "DeletedTextBrush"), result.ExitCode == 0);
        }

        if (output.Text.Length > 0)
        {
            this.AppendOutput(output.Text + Environment.NewLine);
        }
    }

    private void AppendOutput(string text)
    {
        if (this.OutputBox.Text.Length + text.Length > 1024 * 1024)
        {
            this.OutputBox.Text = this.OutputBox.Text[Math.Min(this.OutputBox.Text.Length, 256 * 1024)..];
        }

        this.OutputBox.AppendText(text);
        this.OutputBox.ScrollToEnd();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await this.RunAsync();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!this._isRunning)
        {
            this.Close();
            return;
        }

        this._cancellation?.Cancel();
        this.CloseButton.IsEnabled = false;
        this.StatusText.Text = L10n.T("Остановка…");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (this._isRunning)
        {
            e.Cancel = true;
            this._closeRequested = true;
            this._cancellation?.Cancel();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(this._root) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            this.StatusText.Text = L10n.T("Не удалось открыть папку.");
        }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "preparation.txt" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, this.OutputBox.Text, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            this.StatusText.Text = L10n.T("Не удалось сохранить журнал.");
        }
    }
}
