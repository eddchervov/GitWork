using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GitWork.App;

public partial class App : Application
{
    private void CopyLabel_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock label || string.IsNullOrEmpty(label.Text))
        {
            return;
        }

        DependencyObject? ancestor = label;
        while (ancestor is not null)
        {
            if (ancestor is MenuItem or ContextMenu or ButtonBase or ComboBoxItem
                || ancestor is FrameworkElement { ContextMenu: not null })
            {
                return;
            }

            ancestor = ancestor is Visual ? VisualTreeHelper.GetParent(ancestor) : LogicalTreeHelper.GetParent(ancestor);
        }

        var menu = new ContextMenu { PlacementTarget = label, Placement = PlacementMode.MousePoint, StaysOpen = false };
        var copy = new MenuItem { Header = L10n.T("Копировать текст") };
        copy.Click += this.CopyLabel_Click;
        menu.Items.Add(copy);
        menu.Opened += (_, _) => copy.Focus();
        e.Handled = true;
        menu.IsOpen = true;
    }

    private void CopyLabel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu { PlacementTarget: TextBlock label })
        {
            return;
        }

        try
        {
            Clipboard.SetText(label.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(L10n.T("Буфер обмена занят. Повторите копирование."), "Git Work", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => App.WriteCrashLog(args.ExceptionObject);
        EventManager.RegisterClassHandler(typeof(TextBlock), UIElement.MouseRightButtonUpEvent, new MouseButtonEventHandler(this.CopyLabel_MouseRightButtonUp));
        try
        {
            var settings = AppSettings.Load();
            L10n.SetLanguage(settings.Language);
            ThemeManager.Apply(settings.Theme);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            L10n.SetLanguage("ru");
            ThemeManager.Apply("Dark");
        }

        base.OnStartup(e);
    }

    private static void WriteCrashLog(object exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitWork", "logs");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "last-crash.log"), $"{DateTimeOffset.Now:O}\n{exception}", new System.Text.UTF8Encoding(false));
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(logError);
        }
    }
}
