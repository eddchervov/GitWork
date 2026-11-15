using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GitWork.App;

internal static partial class WindowAppearance
{
    public static void ApplyTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var enabled = ThemeManager.CurrentTheme == "Dark" ? 1 : 0;
        WindowAppearance.DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
