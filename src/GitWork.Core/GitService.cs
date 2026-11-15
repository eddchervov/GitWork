using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitWork.Core;

public sealed class GitService
{
    public Task<string> RunAsync(string directory, params string[] arguments) => this.RunProcessAsync(directory, null, arguments);

    public Task<string> RunWithTimeoutAsync(string directory, TimeSpan timeout, params string[] arguments)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return this.RunProcessAsync(directory, null, arguments, timeout);
    }

    public Task<string> RunLimitedAsync(string directory, int maximumOutputLength, params string[] arguments)
    {
        if (maximumOutputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputLength));
        }

        return this.RunProcessAsync(directory, maximumOutputLength, arguments);
    }

    private async Task<string> RunProcessAsync(string directory, int? maximumOutputLength, string[] arguments, TimeSpan? timeoutDuration = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new GitException($"Не удалось запустить Git. Проверьте установку Git for Windows и PATH. {exception.Message}");
        }

        var outputTask = maximumOutputLength is { } limit
            ? GitService.ReadLimitedAsync(process.StandardOutput, limit)
            : process.StandardOutput.ReadToEndAsync();

        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(timeoutDuration ?? TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            throw new GitException("Git не завершил операцию за отведённое время. Проверьте состояние репозитория перед повтором.");
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new GitException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        return output;
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maximumOutputLength)
    {
        var output = new StringBuilder(Math.Min(maximumOutputLength, 16_384));
        var buffer = new char[8_192];
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            var remaining = maximumOutputLength - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return output.ToString();
    }

    public async Task<IReadOnlyList<WorktreeInfo>> GetWorktreesAsync(string repository)
    {
        var output = await this.RunAsync(repository, "worktree", "list", "--porcelain", "-z");

        return GitOutputParser.ParseWorktrees(output);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetStatusAsync(string worktree, bool includeAllUntrackedFiles = false)
    {
        var output = await this.RunAsync(worktree, "status", "--porcelain=v1", "-z",
            includeAllUntrackedFiles ? "--untracked-files=all" : "--untracked-files=normal");

        return GitOutputParser.ParseStatus(output);
    }

    public async Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(string repository, IReadOnlyList<WorktreeInfo> worktrees)
    {
        var output = await this.RunAsync(repository, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes");
        var result = new List<BranchInfo>();

        foreach (var reference in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var isRemote = reference.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = reference[(isRemote ? 13 : 11)..];

            if (isRemote && name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var owner = isRemote ? null : worktrees.FirstOrDefault(tree => tree.Branch == name);
            result.Add(new BranchInfo(name, reference, isRemote, owner?.Path));
        }

        return result.OrderBy(branch => branch.IsRemote).ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> GetRepositoryRootAsync(string folder)
    {
        var output = await this.RunAsync(folder, "rev-parse", "--show-toplevel");

        return System.IO.Path.GetFullPath(output.TrimEnd('\r', '\n'));
    }

    public async Task SwitchBranchAsync(string worktree, string branch)
    {
        await this.RunAsync(worktree, "check-ref-format", "--branch", branch);
        var changes = await this.GetStatusAsync(worktree);

        if (changes.Count > 0)
        {
            throw new GitException("В рабочей копии есть изменения. Сохраните их перед переключением ветки.");
        }

        await this.RunAsync(worktree, "switch", "--no-guess", branch);
    }

    public async Task CreateBranchAsync(string worktree, string branch, string startPoint)
    {
        var normalizedBranch = (await this.RunAsync(worktree, "check-ref-format", "--branch", branch)).TrimEnd('\r', '\n');

        if (!string.Equals(normalizedBranch, branch, StringComparison.Ordinal))
        {
            throw new GitException("Укажите полное имя новой ветки. Git распознал его как другое имя.");
        }

        if (string.IsNullOrWhiteSpace(startPoint)
            || startPoint != "HEAD"
                && !startPoint.StartsWith("refs/heads/", StringComparison.Ordinal)
                && !startPoint.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            throw new GitException("Выберите HEAD или исходную ветку из списка.");
        }

        var changes = await this.GetStatusAsync(worktree);

        if (changes.Count > 0)
        {
            throw new GitException("В рабочей копии есть изменения. Сохраните их перед созданием и переключением на новую ветку.");
        }

        var existingBranches = await this.RunAsync(worktree, "for-each-ref", "--format=%(refname)", "refs/heads");

        if (existingBranches.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(reference => string.Equals(reference, $"refs/heads/{branch}", StringComparison.Ordinal)))
        {
            throw new GitException($"Локальная ветка «{branch}» уже существует.");
        }

        var sourceHash = (await this.RunAsync(worktree, "rev-parse", "--verify", "--end-of-options", $"{startPoint}^{{commit}}"))
            .TrimEnd('\r', '\n');

        if (string.IsNullOrWhiteSpace(sourceHash))
        {
            throw new GitException("Исходная ветка не указывает на коммит.");
        }

        await this.RunAsync(worktree, "switch", "--no-track", "-c", branch, sourceHash);
    }

    public async Task CreateWorktreeAsync(string repository, string targetPath, string branch, string? startPoint)
    {
        await this.ValidateWorktreeCreationAsync(repository, targetPath, branch, startPoint);
        var fullPath = System.IO.Path.GetFullPath(targetPath);

        if (startPoint is null)
        {
            await this.RunAsync(repository, "worktree", "add", "--", fullPath, branch);
            return;
        }

        await this.RunAsync(repository, "worktree", "add", "-b", branch, "--", fullPath, startPoint);
    }

    public async Task ValidateWorktreeCreationAsync(string repository, string targetPath, string branch, string? startPoint)
    {
        var fullPath = System.IO.Path.GetFullPath(targetPath);

        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new GitException("Указанная папка уже существует. Выберите новое имя, чтобы сохранить существующие файлы.");
        }

        await this.RunAsync(repository, "check-ref-format", "--branch", branch);
        var worktrees = await this.GetWorktreesAsync(repository);

        if (worktrees.Any(tree => tree.Branch == branch))
        {
            throw new GitException("Эта ветка уже занята в другом worktree.");
        }

        if (startPoint is null)
        {
            await this.RunAsync(repository, "show-ref", "--verify", $"refs/heads/{branch}");
            return;
        }

        var branches = await this.GetBranchesAsync(repository, worktrees);

        if (branches.Any(existing => !existing.IsRemote && existing.Name == branch))
        {
            throw new GitException($"Ветка «{branch}» уже существует в {repository}.");
        }

        if (!startPoint.StartsWith("refs/heads/", StringComparison.Ordinal)
            && !startPoint.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            throw new GitException("Выберите исходную ветку из списка.");
        }

        await this.RunAsync(repository, "rev-parse", "--verify", $"{startPoint}^{{commit}}");
    }

    public async Task RemoveWorktreeAsync(string repository, string worktreePath, bool allowUncommittedChanges = false)
    {
        var worktrees = await this.GetWorktreesAsync(repository);
        var target = worktrees.FirstOrDefault(tree => PathsEqual(tree.Path, worktreePath));

        if (target is null || target.IsMain || target.IsBare || target.IsLocked || target.IsPrunable)
        {
            throw new GitException("Эту рабочую копию нельзя удалить: она основная, заблокирована или недоступна.");
        }

        var changes = await this.GetStatusAsync(target.Path);

        if (changes.Count > 0 && !allowUncommittedChanges)
        {
            throw new GitException("В рабочей копии есть изменения. Удаление отменено.");
        }

        await this.RunAsync(repository, "worktree", "remove", "--force", "--", target.Path);
    }

    public async Task<string> GetDiffAsync(string worktree, ChangedFile file)
    {
        if (file.Status == "??")
        {
            return "Новый неотслеживаемый файл. Git пока не хранит его предыдущую версию.";
        }

        var paths = file.OriginalPath is null ? new[] { file.Path } : new[] { file.OriginalPath, file.Path };
        var unstagedArguments = new[] { "diff", "--no-ext-diff", "--no-textconv", "--", }.Concat(paths).ToArray();
        var stagedArguments = new[] { "diff", "--cached", "--no-ext-diff", "--no-textconv", "--" }.Concat(paths).ToArray();
        var unstaged = await this.RunAsync(worktree, unstagedArguments);
        var staged = await this.RunAsync(worktree, stagedArguments);
        var result = $"НЕ ДОБАВЛЕНО В ИНДЕКС\n\n{unstaged}\nДОБАВЛЕНО В ИНДЕКС\n\n{staged}";

        return result.Length > 500_000 ? result[..500_000] + "\n… Показаны первые 500 000 символов." : result;
    }

    public static bool PathsEqual(string first, string second) => string.Equals(
        System.IO.Path.GetFullPath(first).TrimEnd('\\', '/'),
        System.IO.Path.GetFullPath(second).TrimEnd('\\', '/'),
        StringComparison.OrdinalIgnoreCase);
}
