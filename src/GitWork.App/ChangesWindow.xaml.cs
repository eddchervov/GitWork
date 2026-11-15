using System.Windows;
using GitWork.Core;

namespace GitWork.App;

public partial class ChangesWindow : Window
{
    private readonly ChangesView _view;

    public ChangesWindow(string title, IReadOnlyList<ChangeSource> sources, GitService git, string? selectedPath = null, string? selectedWorktree = null, bool autoRefresh = true)
    {
        this.InitializeComponent();
        this._view = new ChangesView(title, sources, git, selectedPath, selectedWorktree, autoRefresh);
        this.Content = this._view;
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Closed += (_, _) => this._view.Detach();
        this.Closing += (_, e) => e.Cancel = this._view.IsBusy;
    }

    public new object FindName(string name) => this._view.FindName(name);

    public bool IsBusy => this._view.IsBusy;

    public Task RefreshAsync() => this._view.RefreshAsync();
}
