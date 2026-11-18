using System.Text.RegularExpressions;

namespace GitWork.Core;

public sealed class GitBranchDeletionService(GitService git)
{
    private readonly GitService _git = git;

    public async Task<BranchDeletionPreview> GetPreviewAsync(string worktree, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new GitException("Выберите локальную ветку.");
        }

        var isBare = (await this._git.RunAsync(worktree, "rev-parse", "--is-bare-repository")).TrimEnd('\r', '\n');
        if (isBare == "true")
        {
            throw new GitException("Нельзя удалить ветку из bare-репозитория.");
        }

        var repository = await this._git.GetRepositoryRootAsync(worktree);
        var normalizedName = (await this._git.RunAsync(repository, "check-ref-format", "--branch", branchName)).TrimEnd('\r', '\n');
        if (!string.Equals(normalizedName, branchName, StringComparison.Ordinal))
        {
            throw new GitException("Укажите полное имя локальной ветки.");
        }

        var fullRef = $"refs/heads/{branchName}";
        var trees = await this._git.GetWorktreesAsync(repository);
        var branches = await this._git.GetBranchesAsync(repository, trees);
        var branch = branches.FirstOrDefault(item => !item.IsRemote && item.FullName == fullRef);
        if (branch is null)
        {
            throw new GitException("Удалять можно только существующую локальную ветку.");
        }

        if (trees.Any(tree => tree.Branch == branchName && GitService.PathsEqual(tree.Path, worktree)))
        {
            throw new GitException("Нельзя удалить текущую ветку.");
        }

        if (branch.OccupiedPath is not null)
        {
            throw new GitException("Нельзя удалить ветку, которая занята в другом worktree.");
        }

        var hash = (await this._git.RunAsync(repository, "rev-parse", "--verify", "--end-of-options", $"{fullRef}^{{commit}}"))
            .TrimEnd('\r', '\n');
        var upstream = (await this._git.RunAsync(repository, "for-each-ref", "--format=%(upstream)", fullRef)).TrimEnd('\r', '\n');
        var existingUpstream = upstream.Length == 0 ? ""
            : (await this._git.RunAsync(repository, "for-each-ref", "--format=%(refname)", upstream)).TrimEnd('\r', '\n');
        var mergeTarget = existingUpstream.Length == 0 ? "HEAD" : upstream;
        var mergedBranches = await this._git.RunAsync(worktree, "branch", "--merged", mergeTarget, "--format=%(refname)");
        var isMerged = mergedBranches.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(fullRef, StringComparer.Ordinal);

        return new BranchDeletionPreview(branchName, fullRef, hash, isMerged);
    }

    public async Task DeleteAsync(string worktree, BranchDeletionPreview preview, bool force = false)
    {
        if (!string.Equals(preview.FullRef, $"refs/heads/{preview.Name}", StringComparison.Ordinal))
        {
            throw new GitException("Предпросмотр относится не к локальной ветке.");
        }

        var currentPreview = await this.GetPreviewAsync(worktree, preview.Name);
        if (!string.Equals(currentPreview.Hash, preview.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Ветка изменилась после предпросмотра. Обновите список и повторите действие.");
        }

        if (!currentPreview.IsMerged && !force)
        {
            throw new GitException("Ветка не слита. Для удаления требуется отдельное подтверждение.");
        }

        var repository = await this._git.GetRepositoryRootAsync(worktree);
        var deleted = await this._git.RunAsync(repository, "-c", "core.abbrev=no", "branch", force ? "-D" : "-d", "--", currentPreview.Name);
        var deletedHash = Regex.Matches(deleted, @"\b[0-9a-f]{40,64}\b").LastOrDefault()?.Value;
        if (deletedHash is not null && deletedHash != currentPreview.Hash)
        {
            // Git сам защищает занятые ветки; при внешней смене вершины сохраняем фактически удалённый коммит.
            var recoveryRef = "refs/gitwork/deleted-branches/" + Guid.NewGuid().ToString("N");
            await this._git.RunAsync(repository, "update-ref", recoveryRef, deletedHash);
            try
            {
                await this._git.RunAsync(repository, "update-ref", currentPreview.FullRef, deletedHash, "");
            }
            catch (GitException)
            {
                throw new GitException($"Ветка изменилась во время удаления. Коммит сохранён: {recoveryRef}");
            }

            throw new GitException($"Ветка изменилась во время удаления. Указатель ветки восстановлен, её настройки могли быть удалены. Коммит сохранён: {recoveryRef}");
        }
        var remainingBranches = await this._git.RunAsync(repository, "for-each-ref", "--format=%(refname)", "refs/heads");
        if (remainingBranches.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(currentPreview.FullRef, StringComparer.Ordinal))
        {
            throw new GitException("Git не удалил выбранную ветку.");
        }
    }
}
