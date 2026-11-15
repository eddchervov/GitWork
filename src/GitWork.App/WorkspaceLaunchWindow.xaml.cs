using System.Windows;

namespace GitWork.App;

public partial class WorkspaceLaunchWindow : Window
{
    public WorkspaceLaunchWindow(string name, IReadOnlyList<WorkspaceRepositoryRow> rows)
    {
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.WorkspaceLabel.Text = name;
        this.Targets.ItemsSource = rows;
    }

    public bool ShouldOpenIde => this.OpenIdeOption.IsChecked == true;

    public bool ShouldOpenTerminals => this.OpenTerminalsOption.IsChecked == true;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!this.ShouldOpenIde && !this.ShouldOpenTerminals)
        {
            MessageBox.Show(this, L10n.T("Выберите IDE или терминалы для открытия."));
            return;
        }

        this.DialogResult = true;
    }
}
