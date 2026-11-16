using System.IO;
using System.Text.RegularExpressions;

namespace GitWork.Core;

public sealed class FileHistoryService
{
    private const int MaximumHistoryOutputLength = 2_000_000;
    private const int MaximumFileOutputLength = 1_000_000;
    private const int MaximumPatchOutputLength = 500_000;
    private const int MaximumPageSize = 100;
    private const int MaximumCommitCount = 500;
    private static readonly Regex s_hash = new("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly GitService _git;

    public FileHistoryService(GitService git)
    {
        this._git = git;
    }

    public async Task<IReadOnlyList<FileHistoryEntry>> GetHistoryAsync(string worktree, string relativeFile, int skip = 0, int pageSize = 50)
    {
        var path = FileHistoryService.NormalizeRelativePath(relativeFile);
        if (skip < 0 || skip >= FileHistoryService.MaximumCommitCount || pageSize is < 1 or > FileHistoryService.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), "Запрошена недопустимая страница истории файла.");
        }

        var count = Math.Min(skip + pageSize, FileHistoryService.MaximumCommitCount);
        var output = await this._git.RunLimitedAsync(worktree, FileHistoryService.MaximumHistoryOutputLength + 1,
            "log", "--follow", "--find-renames", $"-n{count}", "--format=%H%x1f%h%x1f%an%x1f%aI%x1f%P%x1f%s%x00",
            "--name-status", "-z", "--", ":(literal)" + path);
        var commits = FileHistoryService.ParseHistory(output);
        var trackedPath = path;
        var entries = new List<FileHistoryEntry>(commits.Count);

        foreach (var commit in commits)
        {
            var change = commit.Changes.FirstOrDefault(item => item.Path == trackedPath || item.NewPath == trackedPath);
            if (change is null)
            {
                continue;
            }

            var revisionPath = change.NewPath ?? change.Path;
            var previousPath = change.Status.StartsWith('R') ? change.Path : null;
            entries.Add(new FileHistoryEntry(
                commit.FullHash,
                commit.ShortHash,
                commit.Subject,
                commit.Author,
                commit.Date,
                revisionPath,
                previousPath,
                commit.FirstParentHash,
                change.Status.StartsWith('A'),
                change.Status.StartsWith('D')));

            if (previousPath is not null)
            {
                trackedPath = previousPath;
            }
        }

        return entries.Skip(skip).Take(pageSize).ToArray();
    }

    public async Task<FileContent> GetSnapshotAsync(string worktree, FileHistoryEntry entry)
    {
        var revision = await this.ResolveCommitAsync(worktree, entry.FullHash);
        var path = FileHistoryService.NormalizeRelativePath(entry.RevisionPath);
        if (entry.IsDeleted)
        {
            return new FileContent("", "Файл удалён этим коммитом; содержимого версии нет.", false);
        }

        var output = await this._git.RunLimitedAsync(worktree, FileHistoryService.MaximumFileOutputLength + 1,
            "cat-file", "blob", $"{revision}:{path}");
        if (output.Contains('\0'))
        {
            return new FileContent("", "Бинарный файл. Текстовая версия недоступна.", true);
        }

        var isTruncated = output.Length > FileHistoryService.MaximumFileOutputLength;
        var content = isTruncated ? output[..FileHistoryService.MaximumFileOutputLength] : output;
        var messages = new List<string>();
        if (content.Length == 0)
        {
            messages.Add("Пустой файл.");
        }

        if (isTruncated)
        {
            messages.Add("Показана часть файла: достигнут предел предпросмотра.");
        }

        if (content.Contains('\uFFFD'))
        {
            messages.Add("В тексте есть символ замены кодировки; проверьте оригинал в IDE.");
        }

        return new FileContent(content, messages.Count == 0 ? null : string.Join(" ", messages), false);
    }

    public async Task<FileDiff> GetCommitDiffAsync(string worktree, FileHistoryEntry entry)
    {
        var revision = await this.ResolveCommitAsync(worktree, entry.FullHash);
        var revisionPath = FileHistoryService.NormalizeRelativePath(entry.RevisionPath);
        var previousPath = entry.PreviousPath is null ? null : FileHistoryService.NormalizeRelativePath(entry.PreviousPath);
        var paths = FileHistoryService.GetPathspecs(revisionPath, previousPath);
        string patch;
        string? verifiedParent = null;

        if (entry.FirstParentHash is { } parent)
        {
            verifiedParent = await this.ResolveCommitAsync(worktree, parent);
            patch = await this._git.RunLimitedAsync(worktree, FileHistoryService.MaximumPatchOutputLength + 1,
                ["diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", verifiedParent, revision, "--", .. paths]);
        }
        else
        {
            patch = await this._git.RunLimitedAsync(worktree, FileHistoryService.MaximumPatchOutputLength + 1,
                ["diff-tree", "--root", "--no-commit-id", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", "-r", "-p", revision, "--", .. paths]);
        }

        var diff = GitChangeService.ParseUnifiedDiff(patch, patch.Length > FileHistoryService.MaximumPatchOutputLength);
        if (!DiffContextLoader.CanLoad(diff))
        {
            return diff;
        }

        return diff with
        {
            LoadContextAsync = () => DiffContextLoader.ReadAsync(
                this._git,
                worktree,
                verifiedParent,
                previousPath ?? revisionPath,
                revision,
                revisionPath)
        };
    }

    public async Task<FileDiff> GetWorkingDiffAsync(string worktree, string relativeFile, FileHistoryEntry entry, string? currentPath = null)
    {
        var normalizedCurrentPath = FileHistoryService.NormalizeRelativePath(currentPath ?? relativeFile);
        var revisionPath = FileHistoryService.NormalizeRelativePath(entry.RevisionPath);
        var root = Path.GetFullPath(worktree);
        FileHistoryService.ValidateWorktreePath(root, normalizedCurrentPath);
        var revision = await this.ResolveCommitAsync(worktree, entry.FullHash);
        var paths = FileHistoryService.GetPathspecs(normalizedCurrentPath, revisionPath);
        var patch = await this._git.RunLimitedAsync(worktree, FileHistoryService.MaximumPatchOutputLength + 1,
            ["diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", revision, "--", .. paths]);

        var diff = GitChangeService.ParseUnifiedDiff(patch, patch.Length > FileHistoryService.MaximumPatchOutputLength);
        if (!DiffContextLoader.CanLoad(diff))
        {
            return diff;
        }

        return diff with
        {
            LoadContextAsync = async () =>
            {
                var context = await DiffContextLoader.ReadRevisionAndFileAsync(
                    this._git,
                    worktree,
                    revision,
                    revisionPath,
                    worktree,
                    normalizedCurrentPath);
                var latest = await this.GetWorkingDiffAsync(worktree, relativeFile, entry, currentPath);
                if (diff.IsBinary != latest.IsBinary || diff.UnifiedText != latest.UnifiedText)
                {
                    throw new GitException("Файл изменился. Обновите diff и повторите раскрытие контекста.");
                }

                return context;
            }
        };
    }

    private async Task<string> ResolveCommitAsync(string worktree, string hash)
    {
        if (!FileHistoryService.s_hash.IsMatch(hash))
        {
            throw new GitException("История содержит неверный идентификатор коммита.");
        }

        var resolved = (await this._git.RunAsync(worktree, "rev-parse", "--verify", "--end-of-options", $"{hash}^{{commit}}"))
            .TrimEnd('\r', '\n');
        if (!string.Equals(resolved, hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Git вернул другой коммит вместо выбранной версии.");
        }

        return resolved;
    }

    private static IReadOnlyList<HistoryCommit> ParseHistory(string output)
    {
        var tokens = output.Split('\0');
        var headerIndexes = new List<int>();
        var headers = new List<HistoryHeader>();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!FileHistoryService.TryParseHeader(tokens[index], out var header))
            {
                continue;
            }

            headerIndexes.Add(index);
            headers.Add(header);
        }

        var commits = new List<HistoryCommit>(headers.Count);
        for (var headerIndex = 0; headerIndex < headers.Count; headerIndex++)
        {
            var header = headers[headerIndex];
            var endIndex = headerIndex + 1 < headerIndexes.Count ? headerIndexes[headerIndex + 1] : tokens.Length;
            var changes = new List<HistoryChange>();

            for (var index = headerIndexes[headerIndex] + 1; index < endIndex; index++)
            {
                var status = tokens[index].Trim('\r', '\n');
                if (!FileHistoryService.IsChangeStatus(status))
                {
                    continue;
                }

                if (status.StartsWith('R') || status.StartsWith('C'))
                {
                    var oldPath = FileHistoryService.ReadNextPath(tokens, ref index, endIndex);
                    var newPath = FileHistoryService.ReadNextPath(tokens, ref index, endIndex);
                    if (oldPath is not null && newPath is not null)
                    {
                        changes.Add(new HistoryChange(status, oldPath, newPath));
                    }

                    continue;
                }

                var path = FileHistoryService.ReadNextPath(tokens, ref index, endIndex);
                if (path is not null)
                {
                    changes.Add(new HistoryChange(status, path, null));
                }
            }

            commits.Add(new HistoryCommit(header.FullHash, header.ShortHash, header.Author, header.Date,
                header.Parents.FirstOrDefault(), header.Subject, changes));
        }

        return commits;
    }

    private static bool TryParseHeader(string token, out HistoryHeader header)
    {
        var value = token.TrimStart('\r', '\n');
        var fields = value.Split('\u001f', 6);
        if (fields.Length != 6 || !FileHistoryService.s_hash.IsMatch(fields[0]))
        {
            header = null!;
            return false;
        }

        var parents = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        header = new HistoryHeader(fields[0], fields[1], fields[2], fields[3], parents, fields[5]);
        return true;
    }

    private static bool IsChangeStatus(string value) => value.Length > 0
        && value[0] is 'A' or 'C' or 'D' or 'M' or 'R' or 'T' or 'U' or 'X' or 'B'
        && value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string? ReadNextPath(string[] tokens, ref int index, int endIndex)
    {
        for (index++; index < endIndex; index++)
        {
            if (tokens[index].Length > 0)
            {
                return tokens[index];
            }
        }

        return null;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new GitException("Выбран неверный путь к файлу.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new GitException("Путь к файлу должен оставаться внутри рабочей копии.");
        }

        return normalized;
    }

    private static string[] GetPathspecs(string path, string? previousPath)
    {
        var paths = previousPath is null || previousPath == path ? [path] : new[] { previousPath, path };
        return paths.Select(item => ":(literal)" + item).ToArray();
    }

    private static void ValidateWorktreePath(string root, string relativePath)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GitException("Просмотр файлов через символические ссылки отключён.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Путь к файлу выходит за пределы рабочей копии.");
        }

        var currentPath = root;
        foreach (var segment in Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.Exists(currentPath) || Directory.Exists(currentPath))
                && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new GitException("Просмотр файлов через символические ссылки отключён.");
            }
        }
    }

    private sealed record HistoryHeader(string FullHash, string ShortHash, string Author, string Date, string[] Parents, string Subject);

    private sealed record HistoryChange(string Status, string Path, string? NewPath);

    private sealed record HistoryCommit(
        string FullHash,
        string ShortHash,
        string Author,
        string Date,
        string? FirstParentHash,
        string Subject,
        IReadOnlyList<HistoryChange> Changes);
}
