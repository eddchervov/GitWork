using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitWork.Core;

namespace GitWork.App;

public partial class GitClientView
{
    private Task? _repositoryMenuSelection;

    private void Repository_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void Repository_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!this.CanLeave || sender is not ListBoxItem { DataContext: RepositoryInfo repository, ContextMenu: { } menu } row)
        {
            return;
        }

        this._updating = true;
        try
        {
            this.RepositoriesList.SelectedItem = repository;
            row.Focus();
        }
        finally
        {
            this._updating = false;
        }

        if (this._selectedRepository is null || !GitService.PathsEqual(this._selectedRepository.Path, repository.Path))
        {
            this._repositoryMenuSelection = this.SelectRepositoryAsync(repository);
        }

        menu.PlacementTarget = row;
        menu.IsOpen = true;
    }

    private void RepositoryHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = !this.CanLeave || this._selectedRepository is null || this._selectedCopy is null;
    }

    private void RepositoryHeaderMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (!this.CanLeave || this._selectedRepository is not { } repository || this._selectedCopy is not { } copy
            || sender is not ContextMenu menu)
        {
            if (sender is ContextMenu invalidMenu)
            {
                invalidMenu.IsOpen = false;
            }

            return;
        }

        this.PopulateRepositoryMenu(menu, repository, copy.Path, false);
    }

    private void RepositoryMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        if ((!this.CanLeave && this._repositoryMenuSelection is not { IsCompleted: false })
            || menu.PlacementTarget is not ListBoxItem { DataContext: RepositoryInfo repository })
        {
            menu.IsOpen = false;
            return;
        }

        this.PopulateRepositoryMenu(menu, repository, repository.Path, true);
    }

    private void PopulateRepositoryMenu(ContextMenu menu, RepositoryInfo repository, string path, bool allowRemove)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = repository.Name, FontWeight = FontWeights.SemiBold } });
        var location = new TextBlock { Text = path, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
        location.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        menu.Items.Add(new MenuItem { IsEnabled = false, Header = location, ToolTip = path });
        var state = this.GetRepositoryState(path);
        if (state.Branch.Length > 0)
        {
            menu.Items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock
            {
                Text = L10n.T("Ветка") + ": " + state.Branch
                    + (state.HasChanges ? " · " + L10n.T("Изменения {0}", state.ChangeCount) : "")
            } });
        }

        menu.Items.Add(new Separator());
        this.AddRepositoryAction(menu, repository, path, "Сведения о репозитории…", "repository-info");
        this.AddRepositoryAction(menu, repository, path, "Копировать путь", "copy-path");
        this.AddRepositoryAction(menu, repository, path, "Копировать название", "copy-name");
        menu.Items.Add(new Separator());
        this.AddRepositoryAction(menu, repository, path, "Открыть историю", "history");
        this.AddRepositoryAction(menu, repository, path, "Открыть папку", "folder");
        this.AddRepositoryAction(menu, repository, path, "Терминал", "terminal");
        this.AddRepositoryAction(menu, repository, path, "IDE", "ide");
        if (allowRemove)
        {
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = L10n.T("Убрать из списка"), Tag = "remove-repository" };
            remove.Click += this.RemoveRepository_Click;
            menu.Items.Add(remove);
        }
    }

    private void AddRepositoryAction(ContextMenu menu, RepositoryInfo repository, string path, string label, string action)
    {
        var item = new MenuItem { Header = L10n.T(label), Tag = action };
        item.Click += async (_, _) => await this.RunRepositoryActionAsync(repository, path, action);
        menu.Items.Add(item);
    }

    private async Task RunRepositoryActionAsync(RepositoryInfo repository, string path, string action)
    {
        if (this._repositoryMenuSelection is { } selection)
        {
            await selection;
        }

        if (!this.CanLeave)
        {
            return;
        }

        if (action is "copy-path" or "copy-name")
        {
            try
            {
                Clipboard.SetText(action == "copy-path" ? path : repository.Name);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                this.SetStatus(L10n.T("Буфер обмена занят. Повторите копирование."));
            }

            return;
        }

        if (action == "history")
        {
            this._updating = true;
            try
            {
                this._mode = "History";
                this.HistoryTab.IsChecked = true;
                if (!this._workspaceContext)
                {
                    this.RepositoriesList.SelectedItem = repository;
                }
            }
            finally
            {
                this._updating = false;
            }

            await this.SelectRepositoryAsync(repository, path);
            return;
        }

        await this.RunAsync(async () =>
        {
            if (action == "repository-info")
            {
                var text = await this.ReadRepositoryInformationAsync(repository, path);
                if (this.IsLoaded)
                {
                    new InformationWindow(L10n.T("Сведения о репозитории"), text) { Owner = Window.GetWindow(this) }.ShowDialog();
                }

                return;
            }

            if (!Directory.Exists(path))
            {
                throw new GitException("Рабочая копия недоступна.");
            }

            var start = action switch
            {
                "ide" => WorkspaceLauncher.PrepareIde(Window.GetWindow(this), path, this._settings.Tools),
                "terminal" => WorkspaceLauncher.PrepareTerminal(path, this._settings.Tools),
                _ => new ProcessStartInfo(path) { UseShellExecute = true }
            };

            if (start is not null)
            {
                Process.Start(start);
            }
        });
    }

    private async Task<string> ReadRepositoryInformationAsync(RepositoryInfo repository, string path)
    {
        var trees = await this._git.GetWorktreesAsync(path);
        var tree = trees.FirstOrDefault(copy => GitService.PathsEqual(copy.Path, path));
        var files = await this._git.GetStatusAsync(path, true);
        var remote = await new GitRemoteService(this._git).ReadAsync(path);
        var text = new StringBuilder();
        text.AppendLine($"{L10n.T("Репозиторий")}: {repository.Name}");
        text.AppendLine($"{L10n.T("Путь")}: {path}");
        text.AppendLine($"{L10n.T("Ветка")}: {tree?.BranchLabel ?? remote.Branch ?? ""}");
        text.AppendLine($"HEAD: {remote.Head ?? L10n.T("Нет коммитов")}");
        text.AppendLine(L10n.T("Незакоммиченных файлов: {0}", files.Count));
        if (remote.UpstreamRef is { } upstream)
        {
            text.AppendLine($"Upstream: {upstream}");
            text.AppendLine($"{L10n.T("К отправке")}: {remote.Ahead} · {L10n.T("К получению")}: {remote.Behind}");
        }

        if (remote.Remotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(L10n.T("Удалённые репозитории"));
            foreach (var item in remote.Remotes)
            {
                text.AppendLine($"{item.Name}: {item.DisplayUrl}");
            }
        }

        text.AppendLine();
        text.AppendLine(L10n.T("Рабочие копии"));
        foreach (var copy in trees)
        {
            text.AppendLine($"{copy.BranchLabel} · {copy.Path}");
        }

        return text.ToString();
    }
}
