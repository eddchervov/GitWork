using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class SelectWorkspaceRepositoriesWindow : Window
{
    private readonly IReadOnlyList<ProjectRepositoryChoice> _choices;

    public SelectWorkspaceRepositoriesWindow(IReadOnlyList<RepositoryInfo> repositories, IEnumerable<string> suggested)
    {
        var paths = suggested.ToArray();
        this._choices = repositories.Select(repository => new ProjectRepositoryChoice(repository,
            paths.Any(path => GitService.PathsEqual(path, repository.Path)))).ToArray();

        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.ApplySearch();
        this.UpdatePrimary();
    }

    public IReadOnlyList<RepositoryInfo> SelectedRepositories => this._choices.Where(choice => choice.IsIncluded).Select(choice => choice.Repository).ToArray();

    public RepositoryInfo? PrimaryRepository => this.PrimaryPicker.SelectedItem as RepositoryInfo;

    private void UpdatePrimary()
    {
        var previous = this.PrimaryRepository;
        this.PrimaryPicker.ItemsSource = this.SelectedRepositories;
        this.PrimaryPicker.SelectedItem = this.SelectedRepositories.FirstOrDefault(repository => repository == previous)
            ?? this.SelectedRepositories.FirstOrDefault();
    }

    private void Choice_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ProjectRepositoryChoice choice } box)
        {
            choice.IsIncluded = box.IsChecked == true;
        }

        if (this.PrimaryPicker is not null)
        {
            this.UpdatePrimary();
        }
    }

    private void ApplySearch()
    {
        var choices = this._choices.Where(choice => SearchMatcher.Matches(this.SearchBox.Text, choice.Repository.Name, choice.Repository.Path)).ToArray();
        var view = new ListCollectionView(choices);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Repository.Group"));
        this.ChoicesList.ItemsSource = view;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (this.ChoicesList is not null)
        {
            this.ApplySearch();
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (this.SelectedRepositories.Count == 0 || this.PrimaryRepository is null)
        {
            this.ValidationText.Text = L10n.T("Выберите хотя бы один репозиторий.");
            return;
        }

        this.DialogResult = true;
    }
}
