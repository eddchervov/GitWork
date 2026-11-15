using System.Windows;

namespace GitWork.App;

public static class ThemeManager
{
    public static string CurrentTheme { get; private set; } = "Dark";

    public static void Apply(string theme)
    {
        ThemeManager.CurrentTheme = theme == "Light" ? "Light" : "Dark";
        var resources = Application.Current.Resources.MergedDictionaries;
        var previous = resources.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Themes/", StringComparison.Ordinal) == true);
        var palette = new ResourceDictionary { Source = new Uri($"/GitWork;component/Themes/{ThemeManager.CurrentTheme}.xaml", UriKind.Relative) };

        if (previous is not null)
        {
            resources[resources.IndexOf(previous)] = palette;
        }
        else
        {
            resources.Insert(0, palette);
        }

        foreach (Window window in Application.Current.Windows)
        {
            WindowAppearance.ApplyTheme(window);
        }
    }
}
