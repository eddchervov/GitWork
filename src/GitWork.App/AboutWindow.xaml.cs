using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace GitWork.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        this.InitializeComponent();
        var version = typeof(App).Assembly.GetName().Version;
        var displayVersion = version is null ? "0.1"
            : version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
        this.VersionText.Text = L10n.T("Версия {0} · ранняя версия", displayVersion);
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
    }

    private void GitHub_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, L10n.T("Не удалось открыть ссылку"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
