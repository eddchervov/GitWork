using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class UsabilityChecks
{
    public static void Run()
    {
        UsabilityChecks.CheckPopupTheme();
        UsabilityChecks.CheckManualRepositoryList();
        UsabilityChecks.CheckFileColors();
        CommitDialogChecks.Run();
        UsabilityChecks.CheckUrlDialog();
    }

    private static void CheckUrlDialog()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "git-work-add-ui-" + Guid.NewGuid().ToString("N"));
        var existing = System.IO.Path.Combine(root, "existing");
        System.IO.Directory.CreateDirectory(existing);
        var marker = System.IO.Path.Combine(existing, "keep.txt");
        System.IO.File.WriteAllText(marker, "keep");
        var dialog = new AddRepositoryWindow(new GitService(), root);
        try
        {
            ((RadioButton)dialog.FindName("UrlMode")).IsChecked = true;
            ((TextBox)dialog.FindName("UrlBox")).Text = "git@github.com:owner/repository.git";
            if (((TextBox)dialog.FindName("FolderNameBox")).Text != "repository"
                || ((Grid)dialog.FindName("UrlPanel")).Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException("Режим URL должен предлагать имя папки из адреса репозитория.");
            }

            ((TextBox)dialog.FindName("FolderNameBox")).Text = "existing";
            ((Button)dialog.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!((TextBlock)dialog.FindName("ValidationText")).Text.Contains("существует", StringComparison.Ordinal)
                || System.IO.File.ReadAllText(marker) != "keep" || dialog.RepositoryPath is not null)
            {
                throw new InvalidOperationException("Диалог должен отказать до клонирования в существующую папку и сохранить её файлы.");
            }
        }
        finally
        {
            dialog.Close();
            System.IO.Directory.Delete(root, true);
        }

        Console.WriteLine("OK: добавление по URL переключает форму и проверяет папку до любых сетевых операций.");
    }

    private static void CheckPopupTheme()
    {
        var previous = ThemeManager.CurrentTheme;
        var label = new TextBlock { Text = "Название коммита" };
        var window = new Window { Width = 340, Height = 160, Content = label, ShowInTaskbar = false };
        try
        {
            window.Show();
            foreach (var theme in new[] { "Dark", "Light", "Dark" })
            {
                ThemeManager.Apply(theme);
                var menu = new ContextMenu { PlacementTarget = label };
                var item = new MenuItem { Header = "Копировать текст" };
                menu.Items.Add(item);
                label.ContextMenu = menu;
                menu.IsOpen = true;
                menu.ApplyTemplate();
                menu.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                item.ApplyTemplate();
                if (menu.Background != window.FindResource("SurfaceBrush") || item.Foreground != window.FindResource("TextBrush"))
                {
                    throw new InvalidOperationException("Всплывающее меню должно использовать фон и текст текущей темы.");
                }

                menu.IsOpen = false;
                label.ContextMenu = null;
            }
        }
        finally
        {
            window.Close();
            ThemeManager.Apply(previous);
        }

        Console.WriteLine("OK: настоящее контекстное меню открывается в обеих темах без циклического стиля TextBlock.");
    }

    private static void CheckManualRepositoryList()
    {
        var view = new GitClientView(new GitService(), new AppSettings { RootPaths = [@"G:\work"] });
        view.SetRepositoriesAsync([]).GetAwaiter().GetResult();
        if (((ListBox)view.FindName("RepositoriesList")).Items.Count != 0 || view.FindName("CopyPicker") is not null)
        {
            throw new InvalidOperationException("Пустой ручной список не должен заполняться автопоиском или показывать выбор другой копии.");
        }

        Console.WriteLine("OK: вкладка репозиториев не подставляет автоматически найденные папки.");
    }

    private static void CheckFileColors()
    {
        foreach (var status in new[] { "Добавлен", "Удалён", "Изменён" })
        {
            var row = new ListBoxItem { Tag = status, Content = status, Style = (Style)Application.Current.FindResource("FileRow") };
            row.ApplyTemplate();
            var resource = status == "Добавлен" ? "FileAddedBrush" : status == "Удалён" ? "FileDeletedBrush" : "FileModifiedBrush";
            if (row.Background != row.FindResource(resource))
            {
                throw new InvalidOperationException("Тип изменения должен задавать фон строки файла.");
            }

            row.IsSelected = true;
            if (row.Background != row.FindResource(resource))
            {
                throw new InvalidOperationException("Выделение не должно скрывать цвет типа изменения файла.");
            }
        }

        Console.WriteLine("OK: цвет фона обозначает добавление, удаление и изменение, сохраняясь при выборе файла.");
    }
}
