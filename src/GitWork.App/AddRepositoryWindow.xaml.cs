using System.IO;
using System.Windows;
using Microsoft.Win32;
using GitWork.Core;

namespace GitWork.App;

public partial class AddRepositoryWindow : Window
{
    private readonly GitService _git;
    private readonly GitCloneService _cloneService;
    private string? _suggestedFolderName;
    private bool _isBusy;

    public AddRepositoryWindow(GitService git, string initialDirectory)
    {
        this._git = git;
        this._cloneService = new GitCloneService(git);
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        var startDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.DiskPathBox.Text = startDirectory;
        this.ParentFolderBox.Text = startDirectory;
        this.Loaded += (_, _) => this.DiskPathBox.Focus();
    }

    public string? RepositoryPath { get; private set; }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        this.ValidationText.Text = string.Empty;

        if (this.DiskMode.IsChecked == true)
        {
            await this.AddFromDiskAsync();
            return;
        }

        await this.CloneFromUrlAsync();
    }

    private async Task AddFromDiskAsync()
    {
        var path = this.DiskPathBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            this.ValidationText.Text = L10n.T("Выберите существующую папку Git-репозитория.");
            return;
        }

        this.SetBusy(true, L10n.T("Проверка репозитория…"));

        try
        {
            this.RepositoryPath = await this._git.GetRepositoryRootAsync(path);
            this.SetBusy(false);
            this.DialogResult = true;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this.ValidationText.Text = L10n.T("В выбранной папке не найден Git-репозиторий. {0}", L10n.T(exception.Message));
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    private async Task CloneFromUrlAsync()
    {
        var parentFolder = this.ParentFolderBox.Text.Trim();
        var folderName = this.FolderNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(folderName)
            || folderName is "." or ".."
            || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(folderName), folderName, StringComparison.Ordinal))
        {
            this.ValidationText.Text = L10n.T("Укажите корректное имя новой папки.");
            return;
        }

        string destination;
        try
        {
            destination = Path.Combine(Path.GetFullPath(parentFolder), folderName);
            GitCloneService.ValidateUrl(this.UrlBox.Text);
            GitCloneService.ValidateDestination(destination);
        }
        catch (Exception exception) when (exception is GitException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            this.ValidationText.Text = exception.Message;
            return;
        }

        this.SetBusy(true, L10n.T("Подготовка к клонированию…"));
        var progress = new Progress<string>(message => this.ProgressText.Text = message);

        try
        {
            await this._cloneService.CloneAsync(this.UrlBox.Text, destination, progress);
            this.RepositoryPath = Path.GetFullPath(destination);
            this.SetBusy(false);
            this.DialogResult = true;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this.ValidationText.Text = exception.Message;
        }
        finally
        {
            this.SetBusy(false);
        }
    }

    private void BrowseDisk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L10n.T("Папка Git-репозитория") };
        var initialDirectory = this.DiskPathBox.Text.Trim();

        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            this.DiskPathBox.Text = dialog.FolderName;
        }
    }

    private void BrowseParent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L10n.T("Папка для нового репозитория") };
        var initialDirectory = this.ParentFolderBox.Text.Trim();

        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            this.ParentFolderBox.Text = dialog.FolderName;
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (this.DiskPanel is null)
        {
            return;
        }

        var isDiskMode = this.DiskMode.IsChecked == true;
        this.DiskPanel.Visibility = isDiskMode ? Visibility.Visible : Visibility.Collapsed;
        this.UrlPanel.Visibility = isDiskMode ? Visibility.Collapsed : Visibility.Visible;
        this.AddButton.Content = L10n.T(isDiskMode ? "Добавить" : "Клонировать");
        this.ValidationText.Text = string.Empty;
    }

    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (this.FolderNameBox is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(this.FolderNameBox.Text)
            || this.FolderNameBox.Text == this._suggestedFolderName)
        {
            this._suggestedFolderName = AddRepositoryWindow.GetRepositoryFolderName(this.UrlBox.Text);
            this.FolderNameBox.Text = this._suggestedFolderName;
        }
    }

    private void SetBusy(bool isBusy, string message = "")
    {
        this._isBusy = isBusy;
        this.FormPanel.IsEnabled = !isBusy;
        this.AddButton.IsEnabled = !isBusy;
        this.CancelButton.IsEnabled = !isBusy;
        this.ProgressPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        this.ProgressText.Text = message;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = this._isBusy;
    }

    private static string GetRepositoryFolderName(string url)
    {
        var value = url.Trim().TrimEnd('/');
        var separatorIndex = value.LastIndexOfAny(['/', ':']);
        var name = separatorIndex >= 0 ? value[(separatorIndex + 1)..] : value;

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? string.Empty : name;
    }
}
