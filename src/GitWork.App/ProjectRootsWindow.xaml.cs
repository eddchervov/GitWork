using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using GitWork.Core;

namespace GitWork.App;

public partial class ProjectRootsWindow : Window
{
    private readonly ObservableCollection<string> _roots;

    public ProjectRootsWindow(IEnumerable<string> roots)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this._roots = new ObservableCollection<string>(roots);
        this.RootsList.ItemsSource = this._roots;
    }

    public List<string> RootPaths => this._roots.ToList();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L10n.T("Добавить папку с репозиториями") };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (this._roots.Any(path => GitService.PathsEqual(path, dialog.FolderName)))
        {
            this.ValidationText.Text = L10n.T("Эта папка уже добавлена.");
            return;
        }

        this._roots.Add(Path.GetFullPath(dialog.FolderName));
        this.ValidationText.Text = "";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (this.RootsList.SelectedItem is string path)
        {
            this._roots.Remove(path);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (this._roots.Count == 0)
        {
            this.ValidationText.Text = L10n.T("Добавьте хотя бы одну папку поиска.");
            return;
        }

        this.DialogResult = true;
    }
}
