using System.Windows.Controls;
using System.Windows.Data;
using GitWork.App;

namespace GitWork.UiChecks;

internal static class LocalizationUiChecks
{
    public static void Run()
    {
        var previousLanguage = L10n.Language;
        var label = new TextBlock();
        var fileName = new TextBlock { Text = "Изменённые файлы.cs" };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(L10n.CurrentLanguage))
        {
            Source = L10n.Instance,
            Converter = L10n.StringConverter,
            ConverterParameter = "Файлы"
        });

        try
        {
            L10n.SetLanguage("ru");
            if (label.Text != "Файлы")
            {
                throw new InvalidOperationException("При русском языке подпись должна отображаться по-русски.");
            }

            L10n.SetLanguage("en");
            if (label.Text != "Files" || fileName.Text != "Изменённые файлы.cs")
            {
                throw new InvalidOperationException("Язык должен обновлять подписи, не изменяя текст имени файла.");
            }
        }
        finally
        {
            L10n.SetLanguage(previousLanguage);
        }

        Console.WriteLine("OK: переключение RU/EN обновляет подписи и сохраняет пользовательский текст.");
    }
}
