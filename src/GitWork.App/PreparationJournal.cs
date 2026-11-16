using System.Globalization;
using System.IO;
using System.Text;
using GitWork.Core;

namespace GitWork.App;

internal sealed class PreparationJournal : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private int _attempt;

    public PreparationJournal()
    {
        var directory = Path.Combine(Path.GetDirectoryName(AppSettings.FilePath)!, "logs", "preparation");
        Directory.CreateDirectory(directory);
        this.FilePath = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        this._writer = new StreamWriter(new FileStream(this.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string FilePath { get; }

    public void Begin(PreparationProfile profile, string root, int startIndex)
    {
        lock (this._gate)
        {
            this._writer.WriteLine();
            this._writer.WriteLine($"=== Подготовка: {profile.Name} ===");
            this._writer.WriteLine($"Начало: {DateTimeOffset.Now:O}");
            this._writer.WriteLine($"Рабочая папка: {root}");
            this._writer.WriteLine($"Попытка: {++this._attempt}; начиная с шага {startIndex + 1}");
        }
    }

    public void Report(PreparationOutput output, PreparationProfile profile)
    {
        lock (this._gate)
        {
            var step = profile.Steps[output.StepIndex];
            if (output.IsStarting)
            {
                this._writer.WriteLine();
                this._writer.WriteLine($"[{output.StepIndex + 1}] {step.Name}");
                this._writer.WriteLine($"Оболочка: {step.Shell}; рабочая папка: {step.WorkingDirectory}");
            }

            if (output.Text.Length > 0)
            {
                var stream = output.IsError ? "stderr" : "stdout";
                this._writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{stream}] {output.Text}");
            }

            if (output.Result is { } result)
            {
                var status = result.IsSkipped ? "Пропущен" : result.IsCanceled ? "Остановлен" : result.ExitCode == 0 ? "Успешно" : "Ошибка";
                this._writer.WriteLine($"Результат шага {output.StepIndex + 1}: {status}; код завершения: {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
            }
        }
    }

    public void Finish(string status)
    {
        lock (this._gate)
        {
            this._writer.WriteLine($"Итог: {status}");
            this._writer.WriteLine($"Завершение: {DateTimeOffset.Now:O}");
        }
    }

    public void Export(string path)
    {
        lock (this._gate)
        {
            this._writer.Flush();
            if (!GitService.PathsEqual(path, this.FilePath))
            {
                File.Copy(this.FilePath, path, true);
            }
        }
    }

    public void Dispose()
    {
        lock (this._gate)
        {
            this._writer.Dispose();
        }
    }
}
