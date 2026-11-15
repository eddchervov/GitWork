using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class GraphUiChecks
{
    public static void Run()
    {
        var rows = GitGraphService.BuildLayout(
        [
            GraphUiChecks.Commit('a', ['b', 'c']),
            GraphUiChecks.Commit('b', ['d']),
            GraphUiChecks.Commit('c', ['d']),
            GraphUiChecks.Commit('d', [])
        ]);
        var window = new GitGraphWindow([], new GitService());
        var list = (ListBox)window.FindName("CommitsList");
        list.ItemsSource = rows.Select(row => new GraphCommitItem(row, 66)).ToArray();
        var root = (Grid)window.Content;
        root.Measure(new Size(1380, 900));
        root.Arrange(new Rect(0, 0, 1380, 900));
        root.UpdateLayout();
        var first = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        var second = list.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem;
        if (first is null || second is null || first.ActualHeight != 56
            || Math.Abs(second.TranslatePoint(new Point(), list).Y - first.TranslatePoint(new Point(), list).Y - 56) > 0.1)
        {
            throw new InvalidOperationException("Строки графа должны соединяться без зазоров.");
        }

        if (first.Foreground is not SolidColorBrush foreground
            || ((SolidColorBrush)window.FindResource("TextBrush")).Color != foreground.Color)
        {
            throw new InvalidOperationException("Названия коммитов должны оставаться читаемыми в текущей теме.");
        }

        if (((Button)window.FindName("FetchButton")).IsEnabled || ((Button)window.FindName("PullButton")).IsEnabled)
        {
            throw new InvalidOperationException("Без репозитория сетевые действия должны быть недоступны.");
        }

        if (((ComboBox)window.FindName("GraphModePicker")).SelectedItem is not GraphModeOption { ShowAll: false })
        {
            throw new InvalidOperationException("Граф должен открываться в компактном режиме.");
        }

        list.ItemsSource = rows.Select(row => new GraphCommitItem(row, 66, IsCompact: true)).ToArray();
        root.UpdateLayout();
        first = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        var mergeToggle = first is null ? null : GraphUiChecks.FindButton(first);
        if (mergeToggle is not { Visibility: Visibility.Visible } || mergeToggle.Content?.ToString() != "+")
        {
            throw new InvalidOperationException("Компактный merge должен показывать кнопку раскрытия влитой истории.");
        }

        var files = (ListBox)window.FindName("CommitFiles");
        var diff = (FrameworkElement)window.FindName("CommitDiff");
        if (files.TranslatePoint(new Point(), root).X <= list.TranslatePoint(new Point(), root).X
            || diff.TranslatePoint(new Point(), root).X <= files.TranslatePoint(new Point(), root).X
            || diff.ActualHeight < root.ActualHeight * 0.7 || !((DiffView)diff).IsUnified)
        {
            throw new InvalidOperationException($"История, файлы и единый diff должны стоять слева направо и занимать высоту окна. Высота diff: {diff.ActualHeight}, unified: {((DiffView)diff).IsUnified}, X: {list.TranslatePoint(new Point(), root).X}/{files.TranslatePoint(new Point(), root).X}/{diff.TranslatePoint(new Point(), root).X}.");
        }

        window.Close();
        Console.WriteLine("OK: граф строит ветвления в непрерывных строках и блокирует сетевые действия без репозитория.");
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button button)
        {
            return button;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (GraphUiChecks.FindButton(VisualTreeHelper.GetChild(root, index)) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static GitGraphCommit Commit(char value, char[] parents) => new(new string(value, 40), new string(value, 7),
        "Коммит " + value, "Автор", "2026-11-15T12:00:00+07:00", parents.Select(parent => new string(parent, 40)).ToArray(),
        value == 'a' ? ["refs/heads/main", "HEAD"] : [], value == 'a');
}
