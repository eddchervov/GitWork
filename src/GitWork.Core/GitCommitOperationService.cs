namespace GitWork.Core;

public sealed class GitCommitOperationService
{
    private readonly GitService _git;

    public GitCommitOperationService(GitService git)
    {
        this._git = git;
    }

    public async Task<GitCommitOperationPreview> PreviewAsync(string worktree, string commitHash)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        var hash = await this.ResolveCommitAsync(root, commitHash);
        var output = await this._git.RunAsync(root, "show", "-s", "--format=%H%x00%P%x00%s%x00%an%x00%aI", hash);
        var fields = output.TrimEnd('\r', '\n').Split('\0');
        if (fields.Length != 5 || !string.Equals(fields[0], hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Не удалось прочитать сведения о выбранном коммите.");
        }

        var branch = await this.ReadCurrentBranchAsync(root);
        var operationBlockReason = await this.GetOperationBlockReasonAsync(root);
        var isClean = (await this._git.GetStatusAsync(root, true)).Count == 0;
        var blockReason = operationBlockReason;
        if (blockReason is null && !isClean)
        {
            blockReason = "В рабочей копии есть изменения. Сохраните их перед операцией.";
        }

        var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var canCreateBranch = operationBlockReason is null;
        var canCherryPick = canCreateBranch && isClean && branch is not null;

        return new GitCommitOperationPreview(root, hash, fields[2], fields[3], fields[4], parents, branch,
            canCreateBranch, canCherryPick, blockReason, parents.Length > 1);
    }

    public async Task<GitCommitOperationResult> CreateBranchAsync(
        string worktree,
        string commitHash,
        string branch,
        bool checkout)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        var hash = await this.ResolveCommitAsync(root, commitHash);
        await this.ValidateBranchNameAsync(root, branch);
        var current = await this.ReadIdentityAsync(root);
        await this.EnsureNoOperationAsync(root, "Создание ветки");
        if (checkout)
        {
            await this.EnsureCleanAsync(root, "Создание и переключение ветки");
        }

        if (await this.BranchExistsAsync(root, branch))
        {
            throw new GitException($"Локальная ветка «{branch}» уже существует.");
        }

        await this.VerifyIdentityAsync(root, current);
        await this._git.RunAsync(root, "branch", "--", branch, hash);

        if (checkout)
        {
            await this.VerifyIdentityAsync(root, current);
            await this._git.RunAsync(root, "switch", "--no-guess", "--", branch);
        }

        var after = await this.ReadIdentityAsync(root);
        return new GitCommitOperationResult(false, false, false, hash, after.Branch,
            checkout ? $"Создана ветка «{branch}» от выбранного коммита и выполнено переключение." : $"Создана ветка «{branch}» от выбранного коммита.");
    }

    public async Task<GitCommitOperationResult> CreateWorktreeFromCommitAsync(
        string repository,
        string commitHash,
        string branch,
        string targetPath)
    {
        var root = await this._git.GetRepositoryRootAsync(repository);
        var hash = await this.ResolveCommitAsync(root, commitHash);
        await this.ValidateBranchNameAsync(root, branch);
        var fullPath = Path.GetFullPath(targetPath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new GitException("Указанная папка уже существует. Выберите новую папку, чтобы сохранить её содержимое.");
        }

        if (await this.BranchExistsAsync(root, branch)
            || (await this._git.GetWorktreesAsync(root)).Any(tree => string.Equals(tree.Branch, branch, StringComparison.Ordinal)))
        {
            throw new GitException($"Ветка «{branch}» уже существует или используется в другом worktree.");
        }

        await this.EnsureNoOperationAsync(root, "Создание worktree");
        await this._git.RunAsync(root, "worktree", "add", "-b", branch, "--", fullPath, hash);
        return new GitCommitOperationResult(false, false, false, hash, branch,
            $"Создан worktree «{branch}» от выбранного коммита.");
    }

    public async Task<GitCommitOperationResult> CherryPickAsync(string worktree, string commitHash, int? mainline = null, string? expectedBranch = null)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        var hash = await this.ResolveCommitAsync(root, commitHash);
        var identity = await this.ReadIdentityAsync(root);
        if (identity.Branch is null)
        {
            throw new GitException("Cherry-pick недоступен в detached HEAD. Сначала выберите локальную ветку.");
        }

        if (expectedBranch is not null && identity.Branch != expectedBranch)
        {
            throw new GitException("Целевая ветка изменилась. Откройте cherry-pick заново и проверьте выбранную ветку.");
        }

        await this.EnsureReadyAsync(root, "Cherry-pick");
        var parents = await this.ReadParentsAsync(root, hash);
        if (parents.Length > 1 && mainline is null)
        {
            throw new GitException("Выберите основного родителя merge-коммита для cherry-pick.");
        }

        if (mainline is not null && (parents.Length < 2 || mainline < 1 || mainline > parents.Length))
        {
            throw new GitException($"Для этого коммита укажите родителя от 1 до {parents.Length}.");
        }

        await this.VerifyIdentityAsync(root, identity);
        var arguments = new List<string> { "-c", "core.editor=true", "cherry-pick", "--no-edit" };
        if (mainline is not null)
        {
            arguments.Add("-m");
            arguments.Add(mainline.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add("--");
        arguments.Add(hash);

        try
        {
            await this._git.RunAsync(root, [.. arguments]);
            return new GitCommitOperationResult(false, false, false, hash, identity.Branch,
                $"Коммит {hash[..7]} применён в ветку «{identity.Branch}».");
        }
        catch (GitException exception)
        {
            var state = await this.ReadCherryPickStateCoreAsync(root, hash, identity.Branch, exception.Message);
            if (!state.IsInProgress)
            {
                throw;
            }

            return state;
        }
    }

    public async Task<GitCommitOperationResult> ContinueCherryPickAsync(string worktree)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        var state = await this.ReadCherryPickStateCoreAsync(root);
        if (!state.IsInProgress)
        {
            throw new GitException("В этой рабочей копии нет активного cherry-pick.");
        }

        if (state.HasConflicts)
        {
            throw new GitException("Сначала разрешите конфликты и подготовьте исправленные файлы.");
        }

        if (state.IsEmpty)
        {
            throw new GitException("Изменения уже присутствуют в целевой ветке. Продолжить пустой cherry-pick нельзя; отмените операцию.");
        }

        await this._git.RunAsync(root, "-c", "core.editor=true", "cherry-pick", "--continue");
        var after = await this.ReadIdentityAsync(root);
        return new GitCommitOperationResult(false, false, false, state.CommitHash, after.Branch,
            "Cherry-pick продолжен: исправления сохранены в новом коммите.");
    }

    public async Task<GitCommitOperationResult> AbortCherryPickAsync(string worktree)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);
        var state = await this.ReadCherryPickStateCoreAsync(root);
        if (!state.IsInProgress)
        {
            throw new GitException("В этой рабочей копии нет активного cherry-pick.");
        }

        await this._git.RunAsync(root, "cherry-pick", "--abort");
        var after = await this.ReadIdentityAsync(root);
        return new GitCommitOperationResult(false, false, false, state.CommitHash, after.Branch,
            "Cherry-pick отменён. Целевая ветка возвращена к исходному коммиту.");
    }

    public async Task<GitCommitOperationResult> ReadCherryPickStateAsync(string worktree)
    {
        var root = await this._git.GetRepositoryRootAsync(worktree);

        return await this.ReadCherryPickStateCoreAsync(root);
    }

    private async Task<GitCommitOperationResult> ReadCherryPickStateCoreAsync(
        string root,
        string? fallbackHash = null,
        string? fallbackBranch = null,
        string? errorMessage = null)
    {
        var marker = await this.ReadGitPathAsync(root, "CHERRY_PICK_HEAD");
        var inProgress = File.Exists(marker);
        var hash = inProgress ? (await File.ReadAllTextAsync(marker)).Trim() : fallbackHash;
        var branch = await this.ReadCurrentBranchAsync(root) ?? fallbackBranch;
        var status = await this._git.GetStatusAsync(root, true);
        var conflicts = status.Any(file => file.Status.Any(character => character == 'U')
            || file.Status is "AA" or "DD");
        var staged = await this._git.RunAsync(root, "diff", "--cached", "--name-only", "-z");
        var isEmpty = inProgress && !conflicts && staged.Length == 0;
        var message = !inProgress ? "Cherry-pick не выполняется."
            : conflicts ? "Есть конфликты. Исправьте файлы и подготовьте их, затем продолжите или отмените cherry-pick."
            : isEmpty ? "Cherry-pick пуст: изменения уже есть в целевой ветке. Его можно отменить."
            : errorMessage is not null ? $"Cherry-pick остановлен: {errorMessage}"
            : "Cherry-pick ожидает продолжения.";

        return new GitCommitOperationResult(conflicts, inProgress, isEmpty, hash, branch, message);
    }

    private async Task<string> ResolveCommitAsync(string worktree, string commitHash)
    {
        if (commitHash.Length is not (40 or 64) || commitHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new GitException("Выберите полный SHA коммита из истории.");
        }

        var resolved = (await this._git.RunAsync(worktree, "rev-parse", "--verify", "--end-of-options", $"{commitHash}^{{commit}}"))
            .TrimEnd('\r', '\n');
        if (!string.Equals(resolved, commitHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Выбранный SHA не совпадает с полным идентификатором существующего коммита.");
        }

        return resolved;
    }

    private async Task ValidateBranchNameAsync(string worktree, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Contains('\0'))
        {
            throw new GitException("Введите имя новой ветки.");
        }

        var normalized = (await this._git.RunAsync(worktree, "check-ref-format", "--branch", branch)).TrimEnd('\r', '\n');
        if (!string.Equals(normalized, branch, StringComparison.Ordinal))
        {
            throw new GitException("Укажите полное имя новой ветки. Git распознал его как другое имя.");
        }
    }

    private async Task<bool> BranchExistsAsync(string worktree, string branch)
    {
        var output = await this._git.RunAsync(worktree, "for-each-ref", "--format=%(refname)", "refs/heads");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(reference => string.Equals(reference, $"refs/heads/{branch}", StringComparison.Ordinal));
    }

    private async Task<string[]> ReadParentsAsync(string worktree, string hash)
    {
        var output = await this._git.RunAsync(worktree, "show", "-s", "--format=%P", hash);

        return output.TrimEnd('\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<string?> ReadCurrentBranchAsync(string worktree)
    {
        try
        {
            var output = (await this._git.RunAsync(worktree, "symbolic-ref", "--quiet", "--short", "HEAD"))
                .TrimEnd('\r', '\n');

            return output.StartsWith("refs/", StringComparison.Ordinal) ? null : output;
        }
        catch (GitException)
        {
            return null;
        }
    }

    private async Task<CommitIdentity> ReadIdentityAsync(string worktree) =>
        new(await this.ReadCurrentBranchAsync(worktree), await this.ReadHeadAsync(worktree));

    private async Task VerifyIdentityAsync(string worktree, CommitIdentity expected)
    {
        if (await this.ReadIdentityAsync(worktree) != expected)
        {
            throw new GitException("Текущая ветка или HEAD изменились. Обновите историю перед повтором.");
        }
    }

    private async Task<string> ReadHeadAsync(string worktree) =>
        (await this._git.RunAsync(worktree, "rev-parse", "--verify", "HEAD")).TrimEnd('\r', '\n');

    private async Task EnsureReadyAsync(string worktree, string operation)
    {
        await this.EnsureNoOperationAsync(worktree, operation);
        await this.EnsureCleanAsync(worktree, operation);
    }

    private async Task EnsureNoOperationAsync(string worktree, string operation)
    {
        var reason = await this.GetOperationBlockReasonAsync(worktree);
        if (reason is not null)
        {
            throw new GitException($"{operation} недоступен. {reason}");
        }
    }

    private async Task EnsureCleanAsync(string worktree, string operation)
    {
        if ((await this._git.GetStatusAsync(worktree, true)).Count > 0)
        {
            throw new GitException($"{operation} недоступен: в рабочей копии есть изменения. Сохраните их перед операцией.");
        }
    }

    private async Task<string?> GetOperationBlockReasonAsync(string worktree)
    {
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
        {
            var path = await this.ReadGitPathAsync(worktree, marker);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return "В репозитории уже выполняется merge, rebase или другая операция.";
            }
        }

        return null;
    }

    private async Task<string> ReadGitPathAsync(string worktree, string marker)
    {
        var output = await this._git.RunAsync(worktree, "rev-parse", "--git-path", marker);

        return Path.GetFullPath(output.TrimEnd('\r', '\n'), worktree);
    }

    private sealed record CommitIdentity(string? Branch, string Head);
}

public sealed record GitCommitOperationPreview(
    string Repository,
    string CommitHash,
    string Subject,
    string Author,
    string CommittedAt,
    IReadOnlyList<string> ParentHashes,
    string? CurrentBranch,
    bool CanCreateBranch,
    bool CanCherryPick,
    string? BlockReason,
    bool IsMerge);

public sealed record GitCommitOperationResult(
    bool HasConflicts,
    bool IsInProgress,
    bool IsEmpty,
    string? CommitHash,
    string? TargetBranch,
    string Message);
