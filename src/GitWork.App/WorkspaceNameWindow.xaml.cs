using System.Windows;

namespace GitWork.App;

public partial class WorkspaceNameWindow : Window
{
    public WorkspaceNameWindow(string name)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.NameBox.Text = name;
        this.Loaded += (_, _) =>
        {
            this.NameBox.Focus();
            this.NameBox.SelectAll();
        };
    }

    public string WorkspaceName => this.NameBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (this.WorkspaceName.Length > 0)
        {
            this.DialogResult = true;
        }
    }
}
