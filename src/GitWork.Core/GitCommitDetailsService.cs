namespace GitWork.Core;

public sealed class GitCommitDetailsService(GitService git)
{
    private readonly GitService _git = git;

    public async Task<IReadOnlyList<ChangedFile>> GetFilesAsync(string worktree, string hash)
    {
        var revision = await this.ResolveAsync(worktree, hash);
        var parents = (await this._git.RunAsync(worktree, "rev-list", "--parents", "-n", "1", revision)).Trim().Split(' ');
        var output = parents.Length > 1
            ? await this._git.RunLimitedAsync(worktree, 4_000_001, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", parents[1], revision, "--")
            : await this._git.RunLimitedAsync(worktree, 4_000_001, "diff-tree", "--root", "--no-commit-id", "--name-status", "-z", "-r", "--find-renames", revision, "--");

        if (output.Length > 4_000_000)
        {
            throw new GitException("Список файлов этого коммита слишком велик для просмотра.");
        }

        return GitChangeService.ParseNameStatus(output);
    }

    public async Task<FileDiff> GetDiffAsync(string worktree, string hash, ChangedFile file)
    {
        var revision = await this.ResolveAsync(worktree, hash);
        var parents = (await this._git.RunAsync(worktree, "rev-list", "--parents", "-n", "1", revision)).Trim().Split(' ');
        var paths = (file.OriginalPath is null ? new[] { file.Path } : new[] { file.OriginalPath, file.Path })
            .Select(path => ":(literal)" + path).ToArray();
        var arguments = parents.Length > 1
            ? new[] { "diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", parents[1], revision, "--" }
            : new[] { "show", "--format=", "--root", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", revision, "--" };
        var patch = await this._git.RunLimitedAsync(worktree, 500_001, [.. arguments, .. paths]);

        var diff = GitChangeService.ParseUnifiedDiff(patch, patch.Length > 500_000);
        if (!DiffContextLoader.CanLoad(diff))
        {
            return diff;
        }

        var parent = parents.Length > 1 ? parents[1] : null;
        var oldPath = file.OriginalPath ?? file.Path;

        return diff with
        {
            LoadContextAsync = () => DiffContextLoader.ReadAsync(
                this._git,
                worktree,
                parent,
                oldPath,
                revision,
                file.Path)
        };
    }

    private async Task<string> ResolveAsync(string worktree, string hash)
    {
        if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit))
        {
            throw new GitException("Выберите коммит из истории.");
        }

        return (await this._git.RunAsync(worktree, "rev-parse", "--verify", "--end-of-options", hash + "^{commit}")).Trim();
    }
}
