using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GitWork.App;

public static class ViewState
{
    public static double ScrollOffset(DependencyObject root) => ViewState.FindScroll(root)?.VerticalOffset ?? 0;

    public static void RestoreScroll(FrameworkElement root, double offset)
    {
        root.Dispatcher.InvokeAsync(() => ViewState.FindScroll(root)?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is UIElement { Visibility: not Visibility.Visible })
        {
            return null;
        }

        if (root is ScrollViewer scroll)
        {
            return scroll;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (ViewState.FindScroll(VisualTreeHelper.GetChild(root, index)) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}
