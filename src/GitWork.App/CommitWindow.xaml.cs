using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class CommitWindow : Window
{
    private readonly GitIndexService _index;
    private readonly GitService _git;
    private readonly List<ChangeSource> _sources;
    private CommitPreview? _preview;
    private bool _isLoading;
    private bool _isWriting;
    private bool _allowClose;
    private string? _lastError;

    public bool CommitAttempted { get; private set; }

    public CommitWindow(IReadOnlyList<ChangeSource> sources, GitService git)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this._git = git;
        this._index = new GitIndexService(git);
        this._sources = sources
            .GroupBy(source => Path.GetFullPath(source.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        this.RepositoryPicker.ItemsSource = this._sources.Select(source => new RepositoryOption(source.Name, source.Path)).ToArray();
        this.RepositoryPicker.SelectedIndex = 0;
        this.Loaded += async (_, _) => await this.LoadPreviewAsync();
        this.Closing += (_, e) => e.Cancel = this._isWriting && !this._allowClose;
    }

    private async void RepositoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this._isLoading && this.IsLoaded)
        {
            this._lastError = null;
            await this.LoadPreviewAsync();
        }
    }

    private async Task LoadPreviewAsync()
    {
        if (this.RepositoryPicker.SelectedItem is not RepositoryOption repository)
        {
            this._preview = null;
            this.StagedFilesList.ItemsSource = Array.Empty<string>();
            this.CommitActionButton.IsEnabled = false;
            this.StatusText.Text = L10n.T("Нет доступных репозиториев.");
            return;
        }

        this._isLoading = true;
        this.RepositoryPicker.IsEnabled = false;
        this.CommitActionButton.IsEnabled = false;
        this.StatusText.Text = L10n.T("Обновление списка подготовленных файлов…");
        try
        {
            this._preview = await this._index.GetCommitPreviewAsync(repository.Path);
            this.StagedFilesList.ItemsSource = this._preview.StagedFiles;
            this.CommitActionButton.IsEnabled = this._preview.StagedFiles.Count > 0 && !this._preview.HasConflicts;
            this.StatusText.Text = this._preview.HasConflicts
                ? L10n.T("В репозитории есть конфликты. Коммит недоступен.")
                : this._preview.StagedFiles.Count == 0
                    ? L10n.T("Нет подготовленных файлов.")
                    : L10n.T("Подготовлено файлов: {0}", this._preview.StagedFiles.Count);
            if (this._lastError is not null)
            {
                this.StatusText.Text += " " + this._lastError;
            }
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this._preview = null;
            this.StagedFilesList.ItemsSource = Array.Empty<string>();
            this.StatusText.Text = exception.Message;
        }
        finally
        {
            this._isLoading = false;
            this.RepositoryPicker.IsEnabled = true;
        }
    }

    private async void CommitAction_Click(object sender, RoutedEventArgs e)
    {
        if (this._isLoading || this._preview is null)
        {
            return;
        }

        this.CommitAttempted = true;
        this._isLoading = true;
        this._isWriting = true;
        this.CommitActionButton.IsEnabled = false;
        this.RepositoryPicker.IsEnabled = false;
        this.StatusText.Text = L10n.T("Создание коммита…");
        try
        {
            var selectedRepository = this.RepositoryPicker.SelectedItem as RepositoryOption;
            if (selectedRepository is null
                || !GitService.PathsEqual(await this._git.GetRepositoryRootAsync(selectedRepository.Path), this._preview.Repository))
            {
                throw new GitException(L10n.T("Выбранный репозиторий изменился. Обновите окно коммита."));
            }

            await this._index.CommitAsync(this._preview, this.MessageBox.Text);
            this._allowClose = true;
            this.DialogResult = true;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this._lastError = exception.Message;
            this.StatusText.Text = exception.Message;
            this.MessageBox.Focus();
            this.MessageBox.SelectAll();
        }
        finally
        {
            this._isWriting = false;
            this._isLoading = false;
            this.RepositoryPicker.IsEnabled = true;
            if (this.IsLoaded)
            {
                await this.LoadPreviewAsync();
            }
        }
    }

    private sealed record RepositoryOption(string Name, string Path)
    {
        public override string ToString() => $"{this.Name} · {this.Path}";
    }
}
