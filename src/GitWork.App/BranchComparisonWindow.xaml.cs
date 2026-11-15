using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class BranchComparisonWindow : Window
{
    private readonly BranchComparisonView _view;

    public BranchComparisonWindow(string path, GitService git)
    {
        this.InitializeComponent();
        this._view = new BranchComparisonView(path, git);
        this.Content = this._view;
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Closed += (_, _) => this._view.Detach();
    }

    public new object FindName(string name) => this._view.FindName(name);
}
