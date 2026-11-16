using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GitWork.Core;

public sealed class PreparationRunner
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private const string TruncatedOutputMarker = "Начало вывода обрезано: сохранён только конец.";
    private static readonly Regex s_variableNameRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<PreparationRunResult> RunAsync(
        PreparationProfile profile,
        string rootPath,
        IReadOnlyDictionary<string, string> environment,
        IProgress<PreparationOutput>? progress = null,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(profile.Steps);
        ArgumentNullException.ThrowIfNull(profile.Variables);

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("Не найдена папка комплекта для запуска подготовки.");
        }

        if (startIndex < 0 || startIndex > profile.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            PreparationRunner.ValidateVariableName(pair.Key);
            if (pair.Value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new ArgumentException("Значения переменных подготовки должны занимать одну строку без нулевых символов.", nameof(environment));
            }

            variables[pair.Key] = pair.Value;
        }

        var secrets = profile.Variables.Where(variable => variable.IsSecret)
            .Select(variable => variables.GetValueOrDefault(variable.Name))
            .OfType<string>()
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
        var results = new List<PreparationStepResult>();
        int? failedStepIndex = null;
        var canceled = false;

        for (var index = startIndex; index < profile.Steps.Count; index++)
        {
            var step = profile.Steps[index] ?? throw new ArgumentException("Профиль содержит пустой шаг.", nameof(profile));
            if (!step.IsEnabled)
            {
                progress?.Report(new PreparationOutput(index, "", IsStarting: true));
                var skipped = new PreparationStepResult(index, step.Name, null, true, "");
                results.Add(skipped);
                progress?.Report(new PreparationOutput(index, "", Result: skipped));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            var output = new StringBuilder();
            string? scriptPath = null;
            progress?.Report(new PreparationOutput(index, "", IsStarting: true));
            try
            {
                PreparationRunner.ValidateStep(step);
                var workingDirectory = PreparationRunner.ResolveWorkingDirectory(root, step.WorkingDirectory);
                var startInfo = PreparationRunner.CreateStartInfo(step, workingDirectory, variables, out scriptPath);
                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    throw new InvalidOperationException("Не удалось запустить команду подготовки.");
                }

                using var canceledRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                });

                var standardOutput = PreparationRunner.ReadOutputAsync(process.StandardOutput, output, index, false, secrets, progress);
                var standardError = PreparationRunner.ReadOutputAsync(process.StandardError, output, index, true, secrets, progress);
                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }

                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await process.WaitForExitAsync(killTimeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        var message = "Не удалось завершить дочерний процесс подготовки после отмены.";
                        output.AppendLine(message);
                        progress?.Report(new PreparationOutput(index, message, true));
                        var canceledStep = new PreparationStepResult(index, step.Name, null, false, output.ToString(), true);
                        results.Add(canceledStep);
                        progress?.Report(new PreparationOutput(index, "", IsError: true, Result: canceledStep));

                        return new PreparationRunResult(results, failedStepIndex, true);
                    }
                }

                await Task.WhenAll(standardOutput, standardError);
                var exitCode = process.ExitCode;
                var completedStep = new PreparationStepResult(index, step.Name, exitCode, false, output.ToString(), canceled);
                results.Add(completedStep);
                progress?.Report(new PreparationOutput(index, "", IsError: exitCode != 0, Result: completedStep));
                if (canceled)
                {
                    break;
                }

                if (exitCode != 0)
                {
                    failedStepIndex ??= index;
                    if (step.StopOnError)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                var safeMessage = PreparationRunner.Redact(exception.Message, secrets);
                output.AppendLine(safeMessage);
                progress?.Report(new PreparationOutput(index, safeMessage, true));
                var failedStep = new PreparationStepResult(index, step.Name, -1, false, output.ToString());
                results.Add(failedStep);
                progress?.Report(new PreparationOutput(index, "", IsError: true, Result: failedStep));
                failedStepIndex ??= index;
                if (step.StopOnError)
                {
                    break;
                }
            }
            finally
            {
                if (scriptPath is not null)
                {
                    try
                    {
                        File.Delete(scriptPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        return new PreparationRunResult(results, failedStepIndex, canceled);
    }

    private static ProcessStartInfo CreateStartInfo(
        PreparationStep step,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        out string? scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var temporaryDirectory = Path.GetTempPath();
        scriptPath = Path.Combine(temporaryDirectory, "git-work-preparation-" + Guid.NewGuid().ToString("N")
            + (step.Shell == "CMD" ? ".cmd" : ".ps1"));
        if (step.Shell == "CMD")
        {
            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            File.WriteAllText(scriptPath, "@echo off\r\nchcp 65001 >nul\r\n" + step.Command + "\r\nexit /b %errorlevel%\r\n", new UTF8Encoding(false));
            startInfo.ArgumentList.Add(scriptPath);
        }
        else
        {
            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            var script = string.Join(Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
                "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)",
                "try {",
                step.Command,
                "if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }",
                "} catch {",
                "[Console]::Error.WriteLine($_.ToString())",
                "exit 1",
                "}");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            startInfo.ArgumentList.Add(scriptPath);
        }

        return startInfo;
    }

    private static async Task ReadOutputAsync(
        StreamReader reader,
        StringBuilder output,
        int stepIndex,
        bool isError,
        IReadOnlyList<string> secrets,
        IProgress<PreparationOutput>? progress)
    {
        var line = new StringBuilder();
        var isLongLine = false;
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0)
            {
                if (isLongLine)
                {
                    line.Append("Длинная строка вывода скрыта: более 8192 символов.");
                }

                if (line.Length > 0)
                {
                    PreparationRunner.AppendOutput(line, output, stepIndex, isError, secrets, progress);
                }

                return;
            }

            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (isLongLine)
                    {
                        line.Append("Длинная строка вывода скрыта: более 8192 символов.");
                    }

                    PreparationRunner.AppendOutput(line, output, stepIndex, isError, secrets, progress);
                    line.Clear();
                    isLongLine = false;
                }
                else if (character != '\r' && !isLongLine)
                {
                    line.Append(character);
                    if (line.Length > 8192)
                    {
                        line.Clear();
                        isLongLine = true;
                    }
                }
            }
        }
    }

    private static void AppendOutput(
        StringBuilder line,
        StringBuilder output,
        int stepIndex,
        bool isError,
        IReadOnlyList<string> secrets,
        IProgress<PreparationOutput>? progress)
    {
        var value = PreparationRunner.Redact(line.ToString(), secrets);
        progress?.Report(new PreparationOutput(stepIndex, value, isError));

        lock (output)
        {
            var lineEnding = Environment.NewLine;
            var entryLength = value.Length + lineEnding.Length;
            if (entryLength + PreparationRunner.TruncatedOutputMarker.Length + lineEnding.Length > PreparationRunner.MaximumOutputCharacters)
            {
                output.Clear();
                return;
            }

            var marker = PreparationRunner.TruncatedOutputMarker + lineEnding;
            var needsMarker = output.Length + entryLength > PreparationRunner.MaximumOutputCharacters;
            if (output.Length > 0 && output.ToString().StartsWith(marker, StringComparison.Ordinal))
            {
                output.Remove(0, marker.Length);
                needsMarker = true;
            }

            while (output.Length + entryLength + (needsMarker ? marker.Length : 0) > PreparationRunner.MaximumOutputCharacters)
            {
                var lineEnd = 0;
                while (lineEnd < output.Length && output[lineEnd] != '\n')
                {
                    lineEnd++;
                }

                if (lineEnd < output.Length)
                {
                    lineEnd++;
                }

                output.Remove(0, lineEnd);
                needsMarker = true;
            }

            if (needsMarker)
            {
                output.Insert(0, marker);
            }

            output.Append(value).Append(lineEnding);
        }
    }

    private static string Redact(string value, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            value = value.Replace(secret, "••••••••", StringComparison.Ordinal);
        }

        return value;
    }

    private static string ResolveWorkingDirectory(string rootPath, string workingDirectory)
    {
        if (Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("Рабочая папка шага должна находиться внутри комплекта.", nameof(workingDirectory));
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, workingDirectory));
        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("Рабочая папка шага выходит за пределы комплекта.", nameof(workingDirectory));
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("Не найдена рабочая папка шага подготовки.");
        }

        var currentPath = rootPath;
        var relativeSegments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in relativeSegments)
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Рабочая папка подготовки не может проходить через ссылку за пределами комплекта.", nameof(workingDirectory));
            }
        }

        return fullPath;
    }

    private static void ValidateStep(PreparationStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Name) || string.IsNullOrWhiteSpace(step.Command))
        {
            throw new ArgumentException("Укажите название и команду каждого шага подготовки.");
        }

        if (step.Shell is not ("CMD" or "PowerShell"))
        {
            throw new ArgumentException("Оболочка шага должна быть CMD или PowerShell.");
        }

        if (string.IsNullOrWhiteSpace(step.WorkingDirectory))
        {
            throw new ArgumentException("Укажите рабочую папку шага подготовки.");
        }
    }

    private static void ValidateVariableName(string name)
    {
        if (!PreparationRunner.s_variableNameRegex.IsMatch(name))
        {
            throw new ArgumentException($"Недопустимое имя переменной среды: {name}");
        }
    }
}
