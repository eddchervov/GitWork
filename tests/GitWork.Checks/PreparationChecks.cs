using System.Text.Json;
using GitWork.Core;

namespace GitWork.Checks;

internal static class PreparationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GitWork-preparation-check-" + Guid.NewGuid().ToString("N"), "Папка с пробелом");
        Directory.CreateDirectory(root);
        var nested = Path.Combine(root, "Online", "Files");
        Directory.CreateDirectory(nested);
        try
        {
            await PreparationChecks.CheckSequentialStepsAsync(root, nested, check);
            await PreparationChecks.CheckShellEnvironmentAsync(root, check);
            await PreparationChecks.CheckLongSecretOutputAsync(root, check);
            await PreparationChecks.CheckFailureHandlingAsync(root, check);
            await PreparationChecks.CheckCancellationAsync(root, check);
            PreparationChecks.CheckSerialization(check);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    private static async Task CheckSequentialStepsAsync(string root, string workingDirectory, Action<bool, string> check)
    {
        var profile = new PreparationProfile
        {
            Name = "Online · Debug",
            Variables = [new PreparationVariable { Name = "TOKEN", IsSecret = true }],
            Steps =
            [
                new PreparationStep { Name = "Первый", WorkingDirectory = "Online/Files", Command = "echo first>order.txt" },
                new PreparationStep { Name = "Секрет", WorkingDirectory = "Online/Files", Command = "echo \"%TOKEN%\">>order.txt & echo \"%TOKEN%\"" },
                new PreparationStep { Name = "Отключён", Command = "exit /b 99", IsEnabled = false },
                new PreparationStep { Name = "Третий", WorkingDirectory = "Online/Files", Command = "echo third>>order.txt" }
            ]
        };
        var events = new List<PreparationOutput>();
        var progress = new InlineProgress<PreparationOutput>(events.Add);
        var secret = "secret \"quoted\" & value!percent%NAME%";
        var result = await new PreparationRunner().RunAsync(profile, root,
            new Dictionary<string, string> { ["TOKEN"] = secret, ["NAME"] = "must-not-expand" }, progress);
        var lines = (await File.ReadAllLinesAsync(Path.Combine(workingDirectory, "order.txt"))).Select(line => line.TrimEnd()).ToArray();
        check(result.Success && lines.SequenceEqual(["first", $"\"{secret}\"", "third"]), "Шаги CMD идут по очереди в выбранной папке, а отключённый шаг пропускается");
        check(events.Any(item => item.IsError == false && !item.Text.Contains(secret, StringComparison.Ordinal)
            && item.Text.Contains("••••••••", StringComparison.Ordinal)), "Вывод команд маскирует секретную переменную");
        check(result.Steps.Single(item => item.IsSkipped).StepIndex == 2, "Результат сохраняет номер отключённого шага");

        var escaped = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Steps = [new PreparationStep { Name = "Выход", WorkingDirectory = "../..", Command = "echo unsafe" }]
        }, root, new Dictionary<string, string>());
        check(!escaped.Success && escaped.FailedStepIndex == 0 && escaped.Steps[0].ExitCode == -1,
            "Рабочая папка подготовки не может выйти за корень комплекта");
    }

    private static async Task CheckShellEnvironmentAsync(string root, Action<bool, string> check)
    {
        var secret = "secret \"quoted\" & ! value";
        var events = new List<PreparationOutput>();
        var result = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Variables = [new PreparationVariable { Name = "CONFIG_PASSWORD", IsSecret = true }],
            Steps = [new PreparationStep { Name = "PowerShell", Shell = "PowerShell", Command = "[Console]::WriteLine($env:CONFIG_PASSWORD)" }]
        }, root, new Dictionary<string, string> { ["CONFIG_PASSWORD"] = secret }, new InlineProgress<PreparationOutput>(events.Add));
        check(result.Success && events.Any(item => item.Text.Contains("••••••••", StringComparison.Ordinal)
            && !item.Text.Contains(secret, StringComparison.Ordinal)), "PowerShell получает переменные напрямую, кавычки не исполняются как код, секрет скрыт в выводе");

        var nonZero = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Steps = [new PreparationStep { Name = "Exit code", Shell = "PowerShell", Command = "cmd.exe /c exit 7" }]
        }, root, new Dictionary<string, string>());
        check(!nonZero.Success && nonZero.FailedStepIndex == 0 && nonZero.Steps[0].ExitCode == 7,
            "PowerShell передаёт ненулевой код завершения нативной команды");

        var writeError = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Steps = [new PreparationStep { Name = "Write-Error", Shell = "PowerShell", Command = "Write-Error 'failure'" }]
        }, root, new Dictionary<string, string>());
        check(!writeError.Success && writeError.FailedStepIndex == 0 && writeError.Steps[0].ExitCode != 0,
            "PowerShell останавливается на ошибке командлета и помечает шаг неуспешным");
    }

    private static async Task CheckLongSecretOutputAsync(string root, Action<bool, string> check)
    {
        const string secret = "secret-crossing-boundary";
        var events = new List<PreparationOutput>();
        var result = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Variables = [new PreparationVariable { Name = "TOKEN", IsSecret = true }],
            Steps = [new PreparationStep
            {
                Name = "Длинные строки",
                Shell = "PowerShell",
                Command = "[Console]::Out.WriteLine(('x' * 8187) + $env:TOKEN + 'tail'); [Console]::Error.WriteLine(('x' * 8187) + $env:TOKEN + 'tail'); Write-Output $env:TOKEN"
            }]
        }, root, new Dictionary<string, string> { ["TOKEN"] = secret }, new InlineProgress<PreparationOutput>(events.Add));
        var output = string.Join("\n", events.Select(item => item.Text));
        check(result.Success && !output.Contains("secret", StringComparison.Ordinal)
            && !output.Contains("boundary", StringComparison.Ordinal) && output.Contains("8192", StringComparison.Ordinal)
            && output.Contains("••••••••", StringComparison.Ordinal),
            "Длинные строки stdout и stderr не раскрывают части секрета на границе буфера");
    }

    private static async Task CheckFailureHandlingAsync(string root, Action<bool, string> check)
    {
        var stopped = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Steps =
            [
                new PreparationStep { Name = "Ошибка", Command = "exit /b 7" },
                new PreparationStep { Name = "Не запускать", Command = "echo wrong > must-not-exist.txt" }
            ]
        }, root, new Dictionary<string, string>());
        check(!stopped.Success && stopped.FailedStepIndex == 0 && stopped.Steps.Count == 1
            && !File.Exists(Path.Combine(root, "must-not-exist.txt")), "Ошибка по умолчанию останавливает профиль и указывает шаг для повтора");

        var continued = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Steps =
            [
                new PreparationStep { Name = "Ошибка", Command = "exit /b 7", StopOnError = false },
                new PreparationStep { Name = "Продолжение", Command = "echo ok > continued.txt" }
            ]
        }, root, new Dictionary<string, string>());
        check(!continued.Success && continued.FailedStepIndex == 0 && continued.Steps.Count == 2
            && File.Exists(Path.Combine(root, "continued.txt")), "Отключение остановки позволяет выполнить следующие шаги, сохраняя номер первой ошибки");
    }

    private static async Task CheckCancellationAsync(string root, Action<bool, string> check)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await new PreparationRunner().RunAsync(new PreparationProfile
        {
            Steps = [new PreparationStep { Name = "Ожидание", Shell = "PowerShell", Command = "Start-Sleep -Seconds 30" }]
        }, root, new Dictionary<string, string>(), cancellationToken: cancellation.Token);
        check(result.IsCanceled && !result.Success && result.Steps.Single().IsCanceled, "Отмена завершает активный процесс PowerShell и отмечает шаг отменённым");
    }

    private static void CheckSerialization(Action<bool, string> check)
    {
        var profile = new PreparationProfile
        {
            Name = "Online · Debug",
            Variables = [new PreparationVariable { Name = "CONFIG_PASSWORD", Value = "DPAPI:encrypted-data", IsSecret = true }]
        };
        var serialized = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<PreparationProfile>(serialized);
        check(restored?.Name == profile.Name && restored.Variables.Single().Value == "DPAPI:encrypted-data"
            && restored.Variables.Single().IsSecret, "Профиль и зашифрованное значение секрета сохраняются в модели настроек");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
