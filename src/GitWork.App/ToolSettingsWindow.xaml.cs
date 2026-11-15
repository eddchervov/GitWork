using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GitWork.App;

public partial class ToolSettingsWindow : Window
{
    private bool _isInitializing = true;

    public ToolSettingsWindow(ToolSettings settings)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        var ides = WorkspaceLauncher.FindIdes();
        var terminals = WorkspaceLauncher.FindTerminals();
        this.IdePicker.ItemsSource = ides;
        this.TerminalPicker.ItemsSource = terminals;
        this.IdePicker.SelectedItem = ides.FirstOrDefault(choice => choice.Kind == settings.IdeKind
            && string.Equals(choice.Path, settings.IdePath, StringComparison.OrdinalIgnoreCase))
            ?? (settings.IdeKind == "Default" ? ides[0] : ides.Last());
        this.TerminalPicker.SelectedItem = terminals.FirstOrDefault(choice => choice.Kind == settings.TerminalKind
            && (string.IsNullOrWhiteSpace(settings.TerminalPath) || string.Equals(choice.Path, settings.TerminalPath, StringComparison.OrdinalIgnoreCase)))
            ?? terminals.Last();
        this._isInitializing = false;
        this.UpdatePaths();

        if ((this.IdePicker.SelectedItem as ToolChoice)?.Kind == "Custom")
        {
            this.IdePathBox.Text = settings.IdePath;
        }

        if ((this.TerminalPicker.SelectedItem as ToolChoice)?.Kind == "Custom")
        {
            this.TerminalPathBox.Text = settings.TerminalPath;
        }
    }

    public ToolSettings Settings { get; private set; } = new();

    private void ToolPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this._isInitializing)
        {
            this.UpdatePaths(ReferenceEquals(sender, this.IdePicker), ReferenceEquals(sender, this.TerminalPicker));
        }
    }

    private void UpdatePaths(bool updateIde = true, bool updateTerminal = true)
    {
        var ide = this.IdePicker.SelectedItem as ToolChoice;
        var terminal = this.TerminalPicker.SelectedItem as ToolChoice;
        if (updateIde)
        {
            this.IdePathBox.Text = ide?.Path ?? "";
            this.IdePathBox.IsReadOnly = ide?.Kind != "Custom";
            this.BrowseIdeButton.IsEnabled = ide?.Kind == "Custom";
        }

        if (updateTerminal)
        {
            this.TerminalPathBox.Text = terminal?.Path ?? "";
            this.TerminalPathBox.IsReadOnly = terminal?.Kind != "Custom";
            this.BrowseTerminalButton.IsEnabled = terminal?.Kind == "Custom";
        }
    }

    private void BrowseIde_Click(object sender, RoutedEventArgs e) => this.Browse(this.IdePathBox);

    private void BrowseTerminal_Click(object sender, RoutedEventArgs e) => this.Browse(this.TerminalPathBox);

    private void Browse(TextBox target)
    {
        var dialog = new OpenFileDialog { Title = L10n.T("Выберите приложение"), Filter = "Приложения|*.exe" };

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (this.IdePicker.SelectedItem is not ToolChoice ide || this.TerminalPicker.SelectedItem is not ToolChoice terminal)
        {
            return;
        }

        try
        {
            if (ide.Kind != "Default")
            {
                WorkspaceLauncher.ValidateExecutable(this.IdePathBox.Text.Trim());
            }

            WorkspaceLauncher.ValidateExecutable(this.TerminalPathBox.Text.Trim());
            this.Settings = new ToolSettings
            {
                IdeKind = ide.Kind,
                IdePath = this.IdePathBox.Text.Trim(),
                TerminalKind = terminal.Kind,
                TerminalPath = this.TerminalPathBox.Text.Trim()
            };
            this.DialogResult = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            this.ValidationText.Text = exception.Message;
        }
    }
}
