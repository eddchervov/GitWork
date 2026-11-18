using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class BranchPickerUiChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "GitWork-branch-picker-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var picker = new BranchPickerView();
        var window = new Window { Content = picker, Width = 480, Height = 600, ShowInTaskbar = false };
        var current = new WorktreeInfo(path, "current", "main", true, false, false, false, null);
        var branches = new[]
        {
            new BranchInfo("feature/free", "refs/heads/feature/free", false, null),
            new BranchInfo("feature/occupied", "refs/heads/feature/occupied", false, Path.Combine(path, "other")),
            new BranchInfo("origin/main", "refs/remotes/origin/main", true, null)
        };
        var selectedCount = 0;
        string? deletionRequested = null;
        picker.BranchSelected += _ => selectedCount++;
        picker.BranchDeleteRequested += branch => deletionRequested = branch.Name;

        try
        {
            picker.SetBranches(branches, current, hasChanges: true);
            window.Show();
            window.UpdateLayout();
            var list = (ListBox)picker.FindName("BranchesList");
            var free = list.Items.Cast<BranchPickerChoice>().Single(choice => choice.Branch.Name == "feature/free");
            var freeItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(free);
            BranchPickerUiChecks.RaiseRightClick(freeItem);
            var freeMenu = freeItem.ContextMenu!;
            var freeMenuItems = freeMenu.Items.Cast<MenuItem>().ToArray();
            if (!Equals(list.SelectedItem, free) || !freeMenu.IsOpen || !list.IsEnabled || selectedCount != 0
                || freeMenuItems.Length != 2 || !freeMenuItems[1].IsEnabled)
            {
                throw new InvalidOperationException("Первый правый клик должен выбрать ветку и открыть меню без переключения; удаление свободной ветки доступно даже при изменениях.");
            }

            if (!freeMenuItems[0].IsEnabled)
            {
                throw new InvalidOperationException("Команда копирования имени ветки должна быть доступна.");
            }

            freeMenu.IsOpen = false;
            freeItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
            });
            picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            if (selectedCount != 0)
            {
                throw new InvalidOperationException("При незакоммиченных изменениях левый клик и Enter не должны переключать ветку.");
            }

            freeMenuItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (deletionRequested != "feature/free")
            {
                throw new InvalidOperationException("Выбор удаления должен передать выбранную ветку владельцу Git-клиента.");
            }

            var occupied = list.Items.Cast<BranchPickerChoice>().Single(choice => choice.Branch.Name == "feature/occupied");
            var occupiedItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(occupied);
            BranchPickerUiChecks.RaiseRightClick(occupiedItem);
            var occupiedMenuItems = occupiedItem.ContextMenu!.Items.Cast<MenuItem>().ToArray();
            if (!Equals(list.SelectedItem, occupied) || !occupiedMenuItems[0].IsEnabled || occupiedMenuItems[1].IsEnabled
                || selectedCount != 0)
            {
                throw new InvalidOperationException("Занятая ветка должна оставаться доступной для копирования, а удаление — быть отключено.");
            }

            occupiedItem.ContextMenu.IsOpen = false;
            var remote = list.Items.Cast<BranchPickerChoice>().Single(choice => choice.Branch.IsRemote);
            var remoteItem = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(remote);
            BranchPickerUiChecks.RaiseRightClick(remoteItem);
            var remoteMenuItems = remoteItem.ContextMenu!.Items.Cast<MenuItem>().ToArray();
            if (!remoteMenuItems[0].IsEnabled || remoteMenuItems[1].IsEnabled)
            {
                throw new InvalidOperationException("Удалённую ветку можно копировать, но нельзя удалять через локальный список.");
            }

            remoteItem.ContextMenu.IsOpen = false;
            var currentCard = (FrameworkElement)picker.FindName("CurrentBranchCard");
            BranchPickerUiChecks.RaiseRightClick(currentCard);
            var currentMenuItems = currentCard.ContextMenu!.Items.Cast<MenuItem>().ToArray();
            if (!currentCard.ContextMenu.IsOpen || !currentMenuItems[0].IsEnabled || currentMenuItems[1].IsEnabled)
            {
                throw new InvalidOperationException("Текущая закреплённая карточка должна копироваться, а её удаление — быть отключено.");
            }

            if (selectedCount != 0)
            {
                throw new InvalidOperationException("Правый клик не должен переключать ветку.");
            }
        }
        finally
        {
            window.Close();
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        Console.WriteLine("OK: контекстное меню веток выбирает строку первым кликом, копирует current/occupied/remote и разрешает удаление только свободной local ветки.");
    }

    private static void RaiseRightClick(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        });
    }
}
