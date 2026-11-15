using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitWork.App;
using GitWork.Core;

namespace GitWork.UiChecks;

internal static class DailyChecks
{
    public static void Run()
    {
        var tree = new WorktreeInfo(@"C:\example", "head", "main", true, false, false, false, null);
        var clean = new WorktreeState(tree, [], null);
        var dirty = new WorktreeState(tree, [new ChangedFile(" M", "a.cs", null), new ChangedFile("UU", "b.cs", null)], null);
        var status = NavigationStatus.Combine([clean, dirty, new WorktreeState(tree, [], "Недоступно"), null]);
        DailyChecks.Assert(status.Changes == 2 && status.Conflicts == 1 && status.Unavailable == 1 && status.Unknown == 1,
            "Сводка различает чистые, конфликтные, недоступные и непроверенные копии.");

        var directory = Path.Combine(Path.GetTempPath(), "Git Work tools " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "test tool.exe");
            File.WriteAllText(executable, "");
            var settings = new ToolSettings { IdeKind = "Custom", IdePath = executable, TerminalKind = "WindowsTerminal", TerminalPath = executable };
            var ide = WorkspaceLauncher.BuildIdeStartInfo(directory, directory, settings);
            var terminal = WorkspaceLauncher.PrepareTerminal(directory, settings);
            DailyChecks.Assert(ide.ArgumentList.SequenceEqual([directory]) && ide.WorkingDirectory == directory
                && terminal.ArgumentList.SequenceEqual(["-d", directory]) && terminal.WorkingDirectory == directory,
                "Пути с пробелами передаются отдельными аргументами без команд оболочки.");

            var dialog = new ToolSettingsWindow(settings);
            var idePath = (TextBox)dialog.FindName("IdePathBox");
            var terminals = (ComboBox)dialog.FindName("TerminalPicker");
            terminals.SelectedIndex = terminals.Items.Count - 1;
            DailyChecks.Assert(idePath.Text == executable, "Смена терминала сохраняет пользовательский путь IDE.");
            dialog.Close();

            var history = new FileHistoryWindow(directory, "example.cs", new GitService());
            ((ListBox)history.FindName("HistoryList")).ItemsSource = new[]
            {
                new FileHistoryEntry(new string('a', 40), "aaaaaaa", "Версия файла", "Автор", "2026-11-15T12:00:00+07:00",
                    "example.cs", null, null, true, false)
            };
            var historyRoot = (Grid)history.Content;
            historyRoot.Measure(new Size(1320, 840));
            historyRoot.Arrange(new Rect(0, 0, 1320, 840));
            historyRoot.UpdateLayout();
            DailyChecks.Assert(((ListBox)history.FindName("HistoryList")).ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem,
                "История строит строки с форматированной датой и тематическими вкладками.");
            history.Close();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine("Успешно: " + message);
    }
}
