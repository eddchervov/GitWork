using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GitWork.Core;

namespace GitWork.App;

public partial class BranchComparisonView : UserControl
{
    private readonly string _repository;
    private readonly GitService _git;
    private readonly GitChangeService _changes;
    private BranchComparison? _comparison;
    private ICollectionView? _filesView;
    private int _selectionVersion;
    private int _comparisonVersion;
    private bool _isInitializing;
    private bool _isRefreshingFiles;

    public ObservableCollection<BranchReference> References { get; } = [];

    public void Detach()
    {
        this._selectionVersion++;
        this._comparisonVersion++;
    }

    public BranchComparisonView(string path, GitService git)
    {
        this.InitializeComponent();
        this.Unloaded += (_, _) => this.Detach();
        this._repository = path;
        this._git = git;
        this._changes = new GitChangeService(git);
        this.BasePicker.ItemsSource = this.References;
        this.TargetPicker.ItemsSource = this.References;
        this.ModePicker.ItemsSource = new[]
        {
            new ComparisonModeOption(BranchComparisonMode.BranchChanges, L10n.T("Изменения ветки")),
            new ComparisonModeOption(BranchComparisonMode.TwoVersions, L10n.T("Разница версий"))
        };
        this.ModePicker.SelectedValue = BranchComparisonMode.BranchChanges;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        this.RefreshButton.IsEnabled = false;
        this.SummaryText.Text = L10n.T("Загрузка веток…");

        try
        {
            var worktrees = await this._git.GetWorktreesAsync(this._repository);
            var branches = await this._git.GetBranchesAsync(this._repository, worktrees);
            this.References.Add(new BranchReference("HEAD", L10n.T("Текущий коммит (HEAD)")));

            foreach (var branch in branches)
            {
                this.References.Add(new BranchReference(branch.FullName, branch.Name + (branch.IsRemote ? L10n.T(" · удалённая") : "")));
            }

            if (this.References.Count < 2)
            {
                this.SummaryText.Text = L10n.T("Нужно минимум две версии для сравнения.");
                return;
            }

            this._isInitializing = true;
            var current = worktrees.FirstOrDefault(tree => GitService.PathsEqual(tree.Path, this._repository))?.Branch;
            var currentReference = branches.FirstOrDefault(branch => branch.Name == current && !branch.IsRemote)?.FullName;
            var remote = await new GitRemoteService(this._git).ReadAsync(this._repository);
            this.TargetPicker.SelectedItem = this.References.First(reference => reference.Ref == "HEAD");
            var baseReference = branches.FirstOrDefault(branch => branch.FullName == remote.UpstreamRef)
                ?? branches.FirstOrDefault(branch => branch.IsRemote && branch.Name.EndsWith("/main", StringComparison.OrdinalIgnoreCase))
                ?? branches.FirstOrDefault(branch => branch.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
                ?? branches.FirstOrDefault(branch => branch.Name.Equals("master", StringComparison.OrdinalIgnoreCase))
                ?? branches.FirstOrDefault(branch => branch.FullName != currentReference)
                ?? branches.First();
            this.BasePicker.SelectedItem = this.References.First(reference => reference.Ref == baseReference.FullName);
            this._isInitializing = false;
            await this.CompareAsync();
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            this.SummaryText.Text = L10n.T("Не удалось загрузить ветки: {0}", L10n.T(ShortError(exception)));
        }
    }

    private void Reference_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this._isInitializing && this.IsLoaded)
        {
            _ = this.CompareAsync();
        }
    }

    private void ModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!this._isInitializing && this.IsLoaded)
        {
            _ = this.CompareAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await this.CompareAsync();

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (this.BasePicker.SelectedItem is not BranchReference baseReference
            || this.TargetPicker.SelectedItem is not BranchReference targetReference)
        {
            return;
        }

        this._isInitializing = true;
        this.BasePicker.SelectedItem = targetReference;
        this.TargetPicker.SelectedItem = baseReference;
        this._isInitializing = false;
        await this.CompareAsync();
    }

    private async Task CompareAsync()
    {
        if (this.BasePicker.SelectedItem is not BranchReference baseReference
            || this.TargetPicker.SelectedItem is not BranchReference targetReference
            || this.ModePicker.SelectedValue is not BranchComparisonMode mode)
        {
            return;
        }

        var version = ++this._comparisonVersion;
        this._selectionVersion++;
        this._comparison = null;
        this._filesView = null;
        this.FilesList.ItemsSource = null;
        this.CommitsList.ItemsSource = null;
        this.FileTitle.Text = L10n.T("Выберите файл");
        this.FilesLabel.Text = L10n.T("Файлы");
        this.DiffViewer.Visibility = Visibility.Collapsed;
        this.LoadingText.Visibility = Visibility.Collapsed;
        this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, null));
        this.EmptyFilesText.Visibility = Visibility.Collapsed;
        this.RefreshButton.IsEnabled = false;
        this.SummaryText.Text = L10n.T("Сравнение…");

        try
        {
            var comparison = await this._changes.CompareBranchesAsync(this._repository, baseReference.Ref, targetReference.Ref, mode);
            if (version != this._comparisonVersion)
            {
                return;
            }

            this._comparison = comparison;
            this.CommitsList.ItemsSource = comparison.Commits;
            this.CommitsExpander.Visibility = comparison.Commits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            this._filesView = CollectionViewSource.GetDefaultView(comparison.Files);
            this._filesView.Filter = this.MatchesCurrentFilter;
            this.FilesList.ItemsSource = this._filesView;
            this.SummaryText.Text = mode == BranchComparisonMode.BranchChanges
                ? L10n.T("{0} → {1} · От общего предка. PR не создаётся.", baseReference.DisplayName, targetReference.DisplayName)
                : L10n.T("{0} → {1} · Прямое сравнение коммитов.", baseReference.DisplayName, targetReference.DisplayName);
            this.FilesLabel.Text = L10n.T("Файлы ({0})", comparison.Files.Count);
            this.SummaryText.ToolTip = mode == BranchComparisonMode.BranchChanges
                ? L10n.T("Показывает изменения {0} от общего предка с {1}. Это только просмотр diff, без создания PR.", targetReference.DisplayName, baseReference.DisplayName)
                : L10n.T("Сравнивает выбранные версии напрямую: {0} → {1}.", baseReference.DisplayName, targetReference.DisplayName);
            this.RefreshFileList(null, true);
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (version == this._comparisonVersion)
            {
                this.SummaryText.Text = L10n.T("Сравнение не выполнено: {0}", L10n.T(ShortError(exception)));
                this.EmptyFilesText.Text = L10n.T("Не удалось загрузить файлы");
                this.EmptyFilesText.Visibility = Visibility.Visible;
                this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, ShortError(exception)));
                this.DiffViewer.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (version == this._comparisonVersion)
            {
                this.RefreshButton.IsEnabled = true;
            }
        }
    }

    private bool MatchesCurrentFilter(object item) => item is ChangedFile file
        && SearchMatcher.Matches(this.SearchBox.Text, file.DisplayPath, file.Path, file.Status);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        this.RefreshFileList(this.FilesList.SelectedItem as ChangedFile, false);
    }

    private void RefreshFileList(ChangedFile? preferred, bool selectFirstIfMissing)
    {
        if (this._filesView is null)
        {
            return;
        }

        this._isRefreshingFiles = true;
        this._filesView.Refresh();
        var visible = this._filesView.Cast<ChangedFile>().ToArray();
        var hasAnyChanges = this._comparison?.Files.Count > 0;
        this.EmptyFilesText.Text = L10n.T(hasAnyChanges == true ? "Файлы не найдены" : "Нет изменений файлов");
        this.EmptyFilesText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var next = preferred is not null && visible.Contains(preferred)
            ? preferred
            : selectFirstIfMissing ? visible.FirstOrDefault() : null;
        this.FilesList.SelectedItem = next;
        this._isRefreshingFiles = false;

        if (next is null)
        {
            this._selectionVersion++;
            this.FileTitle.Text = L10n.T(hasAnyChanges == false ? "Нет изменений"
                : visible.Length == 0 ? "Ничего не найдено" : "Выберите файл");
            this.LoadingText.Visibility = Visibility.Collapsed;
            this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, null));
            this.DiffViewer.Visibility = Visibility.Collapsed;
            return;
        }

        if (!ReferenceEquals(next, preferred) || this.FileTitle.Text == L10n.T("Выберите файл"))
        {
            this.LoadSelectedFileAsync(next);
        }
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isRefreshingFiles)
        {
            return;
        }

        if (this.FilesList.SelectedItem is ChangedFile file)
        {
            this.LoadSelectedFileAsync(file);
        }
    }

    private async void LoadSelectedFileAsync(ChangedFile file)
    {
        if (this._comparison is not { } comparison)
        {
            return;
        }

        var version = ++this._selectionVersion;
        this.DiffViewer.Visibility = Visibility.Visible;
        this.FileTitle.Text = file.DisplayPath;
        this.DiffViewer.FilePath = file.Path;
        this.LoadingText.Visibility = Visibility.Visible;
        this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, L10n.T("Чтение файла…")));

        try
        {
            var diff = await this._changes.GetBranchRevisionAsync(this._repository, comparison, file);
            if (version != this._selectionVersion || comparison != this._comparison
                || this.FilesList.SelectedItem is not ChangedFile selectedFile || selectedFile != file)
            {
                return;
            }

            this.DiffViewer.ShowDiff(diff);
            this.LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is GitException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (version == this._selectionVersion)
            {
                this.DiffViewer.ShowDiff(new FileDiff("", "", "", [], false, ShortError(exception)));
                this.LoadingText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F7 && Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
        {
            this.DiffViewer.NavigateChange(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            this.SearchBox.Focus();
            this.SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && this.SearchBox.IsKeyboardFocusWithin && this.SearchBox.Text.Length > 0)
        {
            this.SearchBox.Clear();
            e.Handled = true;
        }
    }

    private static string ShortError(Exception exception) => exception.Message.Length <= 160
        ? exception.Message
        : exception.Message[..157] + "…";
}

public sealed record BranchReference(string Ref, string DisplayName)
{
    public override string ToString() => this.DisplayName;
}

public sealed record ComparisonModeOption(BranchComparisonMode Mode, string DisplayName)
{
    public override string ToString() => this.DisplayName;
}
