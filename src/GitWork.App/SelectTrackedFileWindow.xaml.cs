using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitWork.Core;

namespace GitWork.App;

public partial class SelectTrackedFileWindow : Window
{
    private readonly IReadOnlyList<string> _files;

    public SelectTrackedFileWindow(IReadOnlyList<string> files)
    {
        this._files = files;
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.FilesList.ItemsSource = files;
        this.Loaded += (_, _) => this.SearchBox.Focus();
    }

    public string? SelectedPath => this.FilesList.SelectedItem as string;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (this.FilesList is not null)
        {
            this.FilesList.ItemsSource = this._files.Where(file => SearchMatcher.Matches(this.SearchBox.Text, file)).ToArray();
        }
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => this.OpenButton.IsEnabled = this.SelectedPath is not null;

    private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => this.Open_Click(sender, e);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (this.SelectedPath is not null)
        {
            this.DialogResult = true;
        }
    }
}
