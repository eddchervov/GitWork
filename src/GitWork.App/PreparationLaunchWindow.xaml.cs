using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class PreparationLaunchWindow : Window
{
    public PreparationLaunchWindow(AppSettings settings, string repositoryPath, string rootPath, bool bundle)
    {
        this.InitializeComponent();
        this.TargetText.Text = rootPath;
        this.Preparation.Configure(settings, repositoryPath, bundle, rootPath);
        ((CheckBox)this.Preparation.FindName("RunCheck")).IsChecked = true;
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
    }

    public PreparationProfile? Profile => this.Preparation.ProfileToRun;

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        ((CheckBox)this.Preparation.FindName("RunCheck")).IsChecked = true;
        if (this.Preparation.RememberSelection())
        {
            this.DialogResult = true;
        }
    }
}
