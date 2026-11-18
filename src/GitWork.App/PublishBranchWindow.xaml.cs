using System.Windows;
using System.Windows.Controls;
using GitWork.Core;

namespace GitWork.App;

public partial class PublishBranchWindow : Window
{
    private readonly GitService _git;
    private readonly string _worktree;
    private bool _isClosed;

    public PublishBranchWindow(GitService git, RemoteRepositoryState state, string? selectedRemote = null)
    {
        this._git = git;
        this._worktree = state.Worktree;
        this.InitializeComponent();
        this.SourceInitialized += (_, _) => WindowAppearance.ApplyTheme(this);
        this.Closed += (_, _) => this._isClosed = true;
        this.SourceText.Text = L10n.T("Локальная ветка: {0} · {1}", state.Branch ?? "", state.Head?[..Math.Min(8, state.Head.Length)] ?? "");
        this.SourceText.ToolTip = state.Head;
        this.WorktreeText.Text = state.Worktree;
        var remotes = state.Remotes.Where(remote => remote.PushUrl is not null).ToArray();
        this.RemotePicker.ItemsSource = remotes;
        this.RemotePicker.SelectedItem = remotes.FirstOrDefault(remote => remote.Name == selectedRemote)
            ?? remotes.FirstOrDefault(remote => remote.Name == "origin") ?? remotes.FirstOrDefault();
        this.BranchNameBox.Text = state.Branch ?? "";
        this.UpdateDestination();
    }

    public string RemoteName => (this.RemotePicker.SelectedItem as GitRemoteInfo)?.Name ?? "";

    public string RemoteBranchName => this.BranchNameBox.Text.Trim();

    private void Destination_Changed(object sender, SelectionChangedEventArgs e) => this.UpdateDestination();

    private void BranchName_Changed(object sender, TextChangedEventArgs e) => this.UpdateDestination();

    private void UpdateDestination()
    {
        if (this.DestinationText is null || this.BranchNameBox is null || this.PublishButton is null)
        {
            return;
        }

        var remote = this.RemotePicker.SelectedItem as GitRemoteInfo;
        this.DestinationText.Text = remote is null ? L10n.T("Выберите сервер для публикации.")
            : L10n.T("Назначение: {0}/{1}\n{2}", remote.Name, this.RemoteBranchName, remote.DisplayPushUrl ?? "");
        this.PublishButton.IsEnabled = remote is not null && this.RemoteBranchName.Length > 0;
        this.ValidationText.Text = "";
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (this.RemoteName.Length == 0 || this.RemoteBranchName.Length == 0)
        {
            return;
        }

        this.PublishButton.IsEnabled = false;
        this.RemotePicker.IsEnabled = false;
        this.BranchNameBox.IsEnabled = false;
        try
        {
            var normalized = (await this._git.RunAsync(this._worktree, "check-ref-format", "--branch", this.RemoteBranchName)).TrimEnd('\r', '\n');
            if (this._isClosed)
            {
                return;
            }

            if (normalized != this.RemoteBranchName)
            {
                this.ValidationText.Text = L10n.T("Введите полное имя ветки на сервере.");
                return;
            }

            this.DialogResult = true;
        }
        catch (GitException exception)
        {
            this.ValidationText.Text = L10n.T(exception.Message);
        }
        finally
        {
            this.RemotePicker.IsEnabled = true;
            this.BranchNameBox.IsEnabled = true;
            this.PublishButton.IsEnabled = this.RemoteName.Length > 0 && this.RemoteBranchName.Length > 0;
        }
    }
}
