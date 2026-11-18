using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitWork.Core;

namespace GitWork.App;

internal sealed class FileContextMenus
{
    private readonly ListBox _list;
    private readonly GitService _git;
    private readonly Func<object, FileMenuTarget?> _resolve;
    private readonly Func<bool> _isBusy;
    private readonly Action<bool>? _setBusy;
    private readonly Func<Task>? _refresh;
    private readonly ContextMenu _menu = new();
    private bool _hasMouseTarget;

    private FileContextMenus(ListBox list, GitService git, Func<object, FileMenuTarget?> resolve,
        Func<bool> isBusy, Action<bool>? setBusy, Func<Task>? refresh)
    {
        this._list = list;
        this._git = git;
        this._resolve = resolve;
        this._isBusy = isBusy;
        this._setBusy = setBusy;
        this._refresh = refresh;
        list.ContextMenu = this._menu;
        list.PreviewMouseRightButtonDown += this.RightButtonDown;
        list.PreviewMouseRightButtonUp += this.RightButtonUp;
        list.ContextMenuOpening += this.MenuOpening;
        list.Unloaded += (_, _) => this._menu.IsOpen = false;
        this._menu.Opened += (_, _) => this.BuildMenu();
    }

    public static void Attach(ListBox list, GitService git, Func<object, FileMenuTarget?> resolve,
        Func<bool> isBusy, Action<bool>? setBusy = null, Func<Task>? refresh = null)
    {
        _ = new FileContextMenus(list, git, resolve, isBusy, setBusy, refresh);
    }

    private bool SelectTarget(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(this._list, source) is not ListBoxItem row)
        {
            return false;
        }

        this._list.SelectedItem = row.DataContext;
        row.Focus();

        return true;
    }

    private void RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        this._hasMouseTarget = this.SelectTarget(e);
        e.Handled = true;
    }

    private void RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!this._hasMouseTarget || !this.SelectTarget(e))
        {
            return;
        }

        this._menu.PlacementTarget = this._list;
        this._menu.IsOpen = true;
    }

    private void MenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = this._list.SelectedItem is null || e.CursorLeft >= 0 && !this._hasMouseTarget;
    }

    private void BuildMenu()
    {
        this._menu.Items.Clear();
        if (this._list.SelectedItem is not { } item || this._resolve(item) is not { } target)
        {
            this._menu.IsOpen = false;
            return;
        }

        try
        {
            var fullPath = GitFileActionsService.GetFullPath(target.Worktree, target.File.Path);
            var exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            var openLabel = target.IsWorkingCopy ? "Открыть файл" : "Открыть рабочий файл";
            this.Add(this._menu, openLabel, "open", target, context =>
            {
                Process.Start(new ProcessStartInfo(GitFileActionsService.GetFullPath(context.Worktree, context.File.Path)) { UseShellExecute = true });
                return Task.CompletedTask;
            }, exists);

            ToolSettings? tools = null;
            string? ideReason = null;
            try
            {
                tools = AppSettings.Load().Tools;
                if (tools.IdeKind == "Default")
                {
                    ideReason = L10n.T("Выберите IDE в настройках программы.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                ideReason = exception.Message;
            }

            var ide = this.Add(this._menu, "Открыть в IDE", "ide", target, context =>
            {
                var settings = AppSettings.Load().Tools;
                Process.Start(WorkspaceLauncher.BuildIdeStartInfo(context.Worktree,
                    GitFileActionsService.GetFullPath(context.Worktree, context.File.Path), settings));
                return Task.CompletedTask;
            }, exists && tools is { IdeKind: not "Default" });
            ide.ToolTip = ideReason;
            this.Add(this._menu, "Показать в проводнике", "reveal", target, context =>
            {
                FileContextMenus.Reveal(GitFileActionsService.GetFullPath(context.Worktree, context.File.Path));
                return Task.CompletedTask;
            });
            this._menu.Items.Add(new Separator());
            this.Add(this._menu, "История файла…", "history", target, context =>
            {
                new FileHistoryWindow(context.Worktree, context.File.OriginalPath ?? context.File.Path, this._git, context.File.Path)
                {
                    Owner = Window.GetWindow(this._list)
                }.ShowDialog();
                return Task.CompletedTask;
            }, !target.File.IsDirectory && !target.File.IsGitLink && target.File.Status != "??");
            this._menu.Items.Add(new Separator());
            this.Add(this._menu, "Копировать полный путь", "copy-full", target, context =>
            {
                Clipboard.SetText(GitFileActionsService.GetFullPath(context.Worktree, context.File.Path));
                this.Report(L10n.T("Путь скопирован."));
                return Task.CompletedTask;
            });
            this.Add(this._menu, "Копировать путь от корня репозитория", "copy-relative", target, context =>
            {
                Clipboard.SetText(context.File.Path);
                this.Report(L10n.T("Путь скопирован."));
                return Task.CompletedTask;
            });
            if (!target.IsWorkingCopy)
            {
                return;
            }

            this._menu.Items.Add(new Separator());
            var canWrite = !this._isBusy() && this._setBusy is not null;
            var ignoreFile = this.Add(this._menu, target.File.IsDirectory ? "Добавить папку в .gitignore" : "Добавить файл в .gitignore",
                "ignore-file", target, context => this.IgnoreAsync(context, context.File.Path, context.File.IsDirectory), canWrite);
            if (target.File.Status != "??")
            {
                ignoreFile.ToolTip = L10n.T("Правило .gitignore не скрывает уже отслеживаемые файлы.");
            }
            var parents = new MenuItem { Header = L10n.T("Добавить родительскую папку в .gitignore"), Tag = "ignore-folder" };
            var path = target.File.Path.Replace('\\', '/').TrimEnd('/');
            while (path.LastIndexOf('/') is var separator && separator >= 0)
            {
                path = path[..separator];
                var folder = path;
                this.Add(parents, folder + "/", "ignore-folder-rule", target, context => this.IgnoreAsync(context, folder, true), canWrite);
            }

            parents.IsEnabled = canWrite && parents.Items.Count > 0;
            this._menu.Items.Add(parents);
            this._menu.Items.Add(new Separator());
            if (target.ToggleIncluded is not null)
            {
                this.Add(this._menu, target.IsIncluded ? "Исключить из коммита" : "Включить в коммит", "toggle-included", target, context =>
                {
                    context.ToggleIncluded?.Invoke();
                    return Task.CompletedTask;
                }, canWrite);
            }

            this.Add(this._menu, "Отменить изменения файла…", "discard", target, this.DiscardAsync,
                canWrite && !target.File.IsDirectory && !target.File.IsGitLink);
        }
        catch (Exception exception) when (FileContextMenus.IsExpected(exception))
        {
            this._menu.IsOpen = false;
            this.Report(L10n.T(exception.Message), isError: true);
        }
    }

    private MenuItem Add(ItemsControl menu, string label, string action, FileMenuTarget target,
        Func<FileMenuTarget, Task> callback, bool enabled = true)
    {
        var item = new MenuItem { Header = L10n.T(label), Tag = action, IsEnabled = enabled };
        item.Click += async (_, args) =>
        {
            args.Handled = true;
            try
            {
                if (!this._list.IsLoaded || !this._list.IsVisible)
                {
                    return;
                }

                FileMenuTarget? context = target;
                if (target.IsWorkingCopy && (action is "discard" or "toggle-included" || action.StartsWith("ignore", StringComparison.Ordinal)))
                {
                    context = this._list.SelectedItem is { } selected ? this._resolve(selected) : null;
                    if (context is null || !context.IsWorkingCopy || context.File.Path != target.File.Path
                        || !GitService.PathsEqual(context.Worktree, target.Worktree))
                    {
                        throw new GitException("Выбранный файл изменился. Откройте меню заново.");
                    }
                }

                await callback(context);
            }
            catch (Exception exception) when (FileContextMenus.IsExpected(exception))
            {
                this.Report(L10n.T(exception.Message), isError: true);
            }
        };
        menu.Items.Add(item);

        return item;
    }

    private Task IgnoreAsync(FileMenuTarget target, string path, bool directory) => this.WriteAsync(target, async () =>
    {
        var rule = await new GitFileActionsService(this._git).AddIgnoreRuleAsync(target.Worktree, path, directory);
        return L10n.T("Правило добавлено в .gitignore: {0}", rule);
    });

    private Task DiscardAsync(FileMenuTarget target) => this.WriteAsync(target, async () =>
    {
        var service = new GitFileActionsService(this._git);
        var preview = await service.GetDiscardPreviewAsync(target.Worktree, target.File);
        var text = L10n.T("Отменить все изменения «{0}», включая подготовленные? Текущая версия файла и версия индекса будут сохранены в резервную копию.", target.File.DisplayPath);
        if (MessageBox.Show(Window.GetWindow(this._list), text, L10n.T("Отмена изменений файла"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return "";
        }

        var backup = await service.DiscardAsync(preview);
        return backup is null ? L10n.T("Изменения файла отменены.") : L10n.T("Изменения отменены. Резервная копия: {0}", backup);
    });

    private async Task WriteAsync(FileMenuTarget target, Func<Task<string>> action)
    {
        if (!target.IsWorkingCopy || this._isBusy() || !this._list.IsEnabled || this._setBusy is null)
        {
            return;
        }

        this._setBusy(true);
        string? message = null;
        try
        {
            message = await action();
        }
        finally
        {
            this._setBusy(false);
            try
            {
                if (this._refresh is not null)
                {
                    await this._refresh();
                }
            }
            catch (Exception exception) when (FileContextMenus.IsExpected(exception))
            {
                message = null;
                this.Report(L10n.T("Не удалось обновить список: {0}", L10n.T(exception.Message)), isError: true);
            }
        }

        if (!string.IsNullOrEmpty(message))
        {
            this.Report(message);
        }
    }

    private void Report(string message, bool isError = false)
    {
        if (Window.GetWindow(this._list)?.FindName("StatusText") is TextBlock status)
        {
            status.Text = message;
            status.ToolTip = message;
        }
        else if (isError && Window.GetWindow(this._list) is { } window)
        {
            MessageBox.Show(window, message, "Git Work", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void Reveal(string fullPath)
    {
        if (File.Exists(fullPath))
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + fullPath);
            Process.Start(start);
            return;
        }

        var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        while (directory is not null && !Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }

        if (directory is null)
        {
            throw new GitException("Папка файла больше не найдена.");
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private static bool IsExpected(Exception exception) => exception is GitException or IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException or JsonException;
}
