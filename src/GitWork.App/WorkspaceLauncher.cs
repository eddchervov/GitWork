using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GitWork.App;

public static class WorkspaceLauncher
{
    public static IReadOnlyList<ToolChoice> FindIdes()
    {
        var result = new List<ToolChoice> { new("Default", L10n.T("Системное приложение"), "") };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        WorkspaceLauncher.AddIfExists(result, "VSCode", "Visual Studio Code", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
        WorkspaceLauncher.AddIfExists(result, "VSCode", "Visual Studio Code", Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"));

        var visualStudio = Path.Combine(programFiles, "Microsoft Visual Studio");

        if (Directory.Exists(visualStudio))
        {
            foreach (var version in WorkspaceLauncher.FindDirectories(visualStudio))
            {
                foreach (var edition in WorkspaceLauncher.FindDirectories(version))
                {
                    WorkspaceLauncher.AddIfExists(result, "VisualStudio", $"Visual Studio {Path.GetFileName(version)} · {Path.GetFileName(edition)}", Path.Combine(edition, "Common7", "IDE", "devenv.exe"));
                }
            }
        }

        var jetBrains = Path.Combine(programFiles, "JetBrains");

        if (Directory.Exists(jetBrains))
        {
            foreach (var directory in WorkspaceLauncher.FindDirectories(jetBrains, "*Rider*"))
            {
                WorkspaceLauncher.AddIfExists(result, "Rider", Path.GetFileName(directory), Path.Combine(directory, "bin", "rider64.exe"));
            }
        }

        var localPrograms = Path.Combine(local, "Programs");

        if (Directory.Exists(localPrograms))
        {
            foreach (var directory in WorkspaceLauncher.FindDirectories(localPrograms, "*Rider*"))
            {
                WorkspaceLauncher.AddIfExists(result, "Rider", Path.GetFileName(directory), Path.Combine(directory, "bin", "rider64.exe"));
            }
        }

        result.Add(new ToolChoice("Custom", L10n.T("Другое приложение…"), ""));

        return result;
    }

    public static IReadOnlyList<ToolChoice> FindTerminals()
    {
        var result = new List<ToolChoice>();
        WorkspaceLauncher.AddIfExists(result, "PowerShell", "Windows PowerShell", WorkspaceLauncher.PowerShellPath);
        WorkspaceLauncher.AddIfExists(result, "PowerShell", "PowerShell 7", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
        WorkspaceLauncher.AddIfExists(result, "WindowsTerminal", "Windows Terminal", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe"));
        WorkspaceLauncher.AddIfExists(result, "GitBash", "Git Bash", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "git-bash.exe"));
        result.Add(new ToolChoice("Custom", L10n.T("Другой терминал…"), ""));

        return result;
    }

    public static ProcessStartInfo? PrepareIde(Window owner, string worktree, ToolSettings settings)
    {
        WorkspaceLauncher.ValidateDirectory(worktree);

        if (settings.IdeKind == "VSCode" || settings.IdeKind == "Custom")
        {
            return WorkspaceLauncher.BuildIdeStartInfo(worktree, worktree, settings);
        }

        var solutions = Directory.EnumerateFiles(worktree, "*.sln*")
            .Where(file => Path.GetExtension(file) is ".sln" or ".slnx").OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
        var target = solutions.Length == 1 ? solutions[0] : null;

        if (target is null && (settings.IdeKind != "Rider" || solutions.Length > 1))
        {
            var picker = new OpenFileDialog
            {
                InitialDirectory = worktree,
                Title = L10n.T("Выберите решение или проект"),
                Filter = L10n.T("Решения и проекты|*.sln;*.slnx;*.csproj;*.fsproj|Все файлы|*.*")
            };

            if (picker.ShowDialog(owner) != true)
            {
                return null;
            }

            target = picker.FileName;
        }

        return WorkspaceLauncher.BuildIdeStartInfo(worktree, target ?? worktree, settings);
    }

    public static ProcessStartInfo BuildIdeStartInfo(string worktree, string target, ToolSettings settings)
    {
        WorkspaceLauncher.ValidateDirectory(worktree);

        if (settings.IdeKind == "Default")
        {
            return new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = worktree };
        }

        WorkspaceLauncher.ValidateExecutable(settings.IdePath);
        var start = new ProcessStartInfo(settings.IdePath) { UseShellExecute = true, WorkingDirectory = worktree };
        start.ArgumentList.Add(target);

        return start;
    }

    public static ProcessStartInfo PrepareTerminal(string worktree, ToolSettings settings)
    {
        WorkspaceLauncher.ValidateDirectory(worktree);
        var executable = string.IsNullOrWhiteSpace(settings.TerminalPath) && settings.TerminalKind == "PowerShell"
            ? WorkspaceLauncher.PowerShellPath : settings.TerminalPath;
        WorkspaceLauncher.ValidateExecutable(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = worktree };

        if (settings.TerminalKind == "WindowsTerminal")
        {
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(worktree);
        }
        else if (settings.TerminalKind == "GitBash")
        {
            start.ArgumentList.Add("--cd=" + worktree);
        }
        else if (settings.TerminalKind == "PowerShell")
        {
            start.ArgumentList.Add("-NoLogo");
        }

        return start;
    }

    public static void ValidateExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Приложение не найдено. Проверьте путь в «Файл → Инструменты».");
        }
    }

    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static void ValidateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Папка рабочей копии недоступна.");
        }
    }

    private static void AddIfExists(List<ToolChoice> result, string kind, string name, string path)
    {
        if (File.Exists(path) && !result.Any(choice => string.Equals(choice.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new ToolChoice(kind, name, path));
        }
    }

    private static string[] FindDirectories(string path, string pattern = "*")
    {
        try
        {
            return Directory.GetDirectories(path, pattern);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
