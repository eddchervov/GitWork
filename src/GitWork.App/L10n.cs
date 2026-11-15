using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows.Data;

namespace GitWork.App;

public sealed class L10n : INotifyPropertyChanged
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_englishTranslations = new(L10n.LoadEnglishTranslations);
    private static string s_language = "ru";

    public static L10n Instance { get; } = new();

    public static string Language => L10n.s_language;

    public static LocalizedTextConverter StringConverter { get; } = new();

    public static NavigationTitleConverter NavigationTitleConverter { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLanguage => L10n.s_language;

    public static event EventHandler? LanguageChanged;

    private L10n()
    {
    }

    public static void SetLanguage(string language)
    {
        var normalizedLanguage = language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        if (L10n.s_language == normalizedLanguage)
        {
            return;
        }

        L10n.s_language = normalizedLanguage;
        L10n.Instance.PropertyChanged?.Invoke(L10n.Instance, new PropertyChangedEventArgs(nameof(L10n.CurrentLanguage)));
        L10n.LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string russianText, params object?[] args)
    {
        var text = L10n.Language == "en"
            && L10n.s_englishTranslations.Value.TryGetValue(russianText, out var translation)
                ? translation
                : russianText;

        if (args.Length == 0)
        {
            return text;
        }

        return string.Format(CultureInfo.CurrentCulture, text, args);
    }

    internal static Binding CreateBinding(string russianText)
    {
        return new Binding(nameof(L10n.CurrentLanguage))
        {
            Source = L10n.Instance,
            Mode = BindingMode.OneWay,
            Converter = L10n.StringConverter,
            ConverterParameter = russianText
        };
    }

    private static IReadOnlyDictionary<string, string> LoadEnglishTranslations()
    {
        const string resourceName = "GitWork.App.Resources.en.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = parameter as string ?? value as string;
        if (text is null)
        {
            return value ?? string.Empty;
        }

        if (text.StartsWith("Изменённых файлов: ", StringComparison.Ordinal)
            && int.TryParse(text["Изменённых файлов: ".Length..], out var changedFiles))
        {
            return L10n.T("Изменённых файлов: {0}", changedFiles);
        }

        var conflictMatch = Regex.Match(text, "^Конфликтов: (\\d+) · изменённых файлов: (\\d+)$");
        if (conflictMatch.Success)
        {
            return L10n.T("Конфликтов: {0} · изменённых файлов: {1}",
                int.Parse(conflictMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(conflictMatch.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        return L10n.T(text);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class NavigationTitleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string title || values[1] is not string kind)
        {
            return string.Empty;
        }

        return kind is "Section" or "Group" or "Groups" ? L10n.T(title) : title;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
