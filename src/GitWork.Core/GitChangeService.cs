using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GitWork.Core;

public sealed class GitChangeService
{
    private readonly GitService _git;
    private const int MaximumPatchLength = 500_000;
    private const int MaximumFileLength = 1_000_000;
    private static readonly Regex s_hunkHeader = new("^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public GitChangeService(GitService git)
    {
        this._git = git;
    }

    public async Task<FileDiff> GetRevisionAsync(string worktree, ChangedFile file, ChangeScope scope)
    {
        var status = await this._git.GetStatusAsync(worktree);
        var current = status.FirstOrDefault(item => item.Path == file.Path || item.OriginalPath == file.Path);
        if (current is not null && GitChangeService.IsConflict(current.Status))
        {
            return new FileDiff("", "", "", [], false, "Файл содержит конфликт слияния. Git не может показать обычный diff до разрешения конфликта.");
        }

        if (current?.Status == "??" || current is null && file.Status == "??")
        {
            if (scope == ChangeScope.Staged)
            {
                return GitChangeService.EmptyDiff("Файл ещё не добавлен в индекс.");
            }

            return await this.ReadUntrackedAsync(worktree, file.Path, file.IsGitLink);
        }

        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3" };
        if (scope == ChangeScope.Staged)
        {
            arguments.Add("--cached");
        }

        arguments.Add("--");
        arguments.AddRange(GitChangeService.GetPathspecs(file));
        var patch = await this._git.RunLimitedAsync(worktree, MaximumPatchLength + 1, [.. arguments]);

        var diff = GitChangeService.ParsePatch(GitChangeService.Limit(patch), patch.Length > MaximumPatchLength);
        if (!DiffContextLoader.CanLoad(diff))
        {
            return diff;
        }

        return diff with
        {
            LoadContextAsync = async () =>
            {
                var context = scope == ChangeScope.Staged
                    ? await DiffContextLoader.ReadHeadAndIndexAsync(this._git, worktree, file.OriginalPath ?? file.Path, file.Path)
                    : await DiffContextLoader.ReadIndexAndFileAsync(this._git, worktree, file.Path, file.Path);
                var latest = await this.GetRevisionAsync(worktree, file, scope);
                GitChangeService.EnsureContextMatches(diff, latest);

                return context;
            }
        };
    }

    public async Task<FileDiff> GetWorkingRevisionAsync(string worktree, ChangedFile file)
    {
        var status = await this._git.GetStatusAsync(worktree, true);
        var current = status.FirstOrDefault(item => item.Path == file.Path && item.OriginalPath == file.OriginalPath);
        if (current is null)
        {
            return GitChangeService.EmptyDiff("Файл больше не изменён. Обновите список.");
        }

        if (GitChangeService.IsConflict(current.Status))
        {
            return GitChangeService.EmptyDiff("Файл содержит конфликт слияния. Git не может показать обычный diff до разрешения конфликта.");
        }

        if (current.Status == "??" || await this.HasNoHeadAsync(worktree))
        {
            return await this.ReadUntrackedAsync(worktree, file.Path, file.IsGitLink);
        }

        var paths = GitChangeService.GetPathspecs(file);
        var patch = await this._git.RunLimitedAsync(worktree, MaximumPatchLength + 1,
            ["diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", "HEAD", "--", .. paths]);

        var diff = GitChangeService.ParsePatch(GitChangeService.Limit(patch), patch.Length > MaximumPatchLength);
        if (!DiffContextLoader.CanLoad(diff))
        {
            return diff;
        }

        return diff with
        {
            LoadContextAsync = async () =>
            {
                var oldPath = file.OriginalPath ?? file.Path;
                var head = await DiffContextLoader.ResolveHeadAsync(this._git, worktree);
                var context = head is null
                    ? new DiffContext([], GitChangeService.SplitLines(await DiffContextLoader.ReadFileAsync(worktree, file.Path)))
                    : await DiffContextLoader.ReadRevisionAndFileAsync(this._git, worktree, head, oldPath, worktree, file.Path);
                var latest = await this.GetWorkingRevisionAsync(worktree, file);
                GitChangeService.EnsureContextMatches(diff, latest);

                return context;
            }
        };
    }

    public async Task<BranchComparison> CompareBranchesAsync(string repository, string baseRef, string targetRef, BranchComparisonMode mode = BranchComparisonMode.BranchChanges)
    {
        var normalizedBase = GitChangeService.NormalizeRef(baseRef);
        var normalizedTarget = GitChangeService.NormalizeRef(targetRef);
        var baseHash = await this.ResolveCommitAsync(repository, normalizedBase);
        var targetHash = await this.ResolveCommitAsync(repository, normalizedTarget);
        var mergeBase = "";
        if (mode != BranchComparisonMode.TwoVersions)
        {
            try
            {
                mergeBase = (await this._git.RunAsync(repository, "merge-base", baseHash, targetHash)).Trim();
            }
            catch (GitException)
            {
                throw new GitException("У выбранных веток нет общей истории. Выберите сравнение двух версий.");
            }
        }

        var counts = (await this._git.RunAsync(repository, "rev-list", "--left-right", "--count", $"{baseHash}...{targetHash}"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length != 2)
        {
            throw new GitException("Git вернул неверный формат сравнения веток.");
        }

        var diffBase = mode == BranchComparisonMode.TwoVersions ? baseHash : mergeBase;
        var fileOutput = await this._git.RunAsync(repository, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", diffBase, targetHash, "--");
        var files = GitChangeService.ParseNameStatus(fileOutput);
        var commitOutput = await this._git.RunAsync(repository, "log", $"--format=%H%x1f%h%x1f%an%x1f%aI%x1f%s", targetHash, "--not", baseHash);
        var commits = GitChangeService.ParseCommits(commitOutput);

        return new BranchComparison(normalizedBase, normalizedTarget, baseHash, targetHash, mergeBase,
            int.Parse(counts[1], CultureInfo.InvariantCulture), int.Parse(counts[0], CultureInfo.InvariantCulture), files, commits, mode);
    }

    public async Task<FileDiff> GetBranchRevisionAsync(string repository, BranchComparison comparison, ChangedFile file)
    {
        var paths = GitChangeService.GetPathspecs(file);
        var patch = await this._git.RunLimitedAsync(repository, MaximumPatchLength + 1,
            ["diff", "--no-ext-diff", "--no-textconv", "--find-renames", "--unified=3", comparison.DiffBaseHash, comparison.TargetHash, "--", .. paths]);

        var diff = GitChangeService.ParsePatch(GitChangeService.Limit(patch), patch.Length > MaximumPatchLength);
        if (!DiffContextLoader.CanLoad(diff))
        {
            return diff;
        }

        return diff with
        {
            LoadContextAsync = () => DiffContextLoader.ReadAsync(
                this._git,
                repository,
                comparison.DiffBaseHash,
                file.OriginalPath ?? file.Path,
                comparison.TargetHash,
                file.Path)
        };
    }

    private async Task<string> ResolveCommitAsync(string repository, string reference)
    {
        var result = await this._git.RunAsync(repository, "rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}");

        return result.Trim();
    }

    private async Task<bool> HasNoHeadAsync(string repository)
    {
        try
        {
            await this._git.RunAsync(repository, "rev-parse", "--verify", "--end-of-options", "HEAD^{commit}");
            return false;
        }
        catch (GitException)
        {
            return true;
        }
    }

    private static string NormalizeRef(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith("-", StringComparison.Ordinal))
        {
            throw new GitException("Выберите существующую ветку для сравнения.");
        }

        if (reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            || reference.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            return reference;
        }

        if (reference == "HEAD")
        {
            return reference;
        }

        return "refs/heads/" + reference;
    }

    private static string[] GetPathspecs(ChangedFile file)
    {
        var paths = file.OriginalPath is null ? [file.Path] : new[] { file.OriginalPath, file.Path };
        return paths.Select(path => ":(literal)" + path).ToArray();
    }

    private static void EnsureContextMatches(FileDiff original, FileDiff latest)
    {
        if (original.IsBinary != latest.IsBinary || original.UnifiedText != latest.UnifiedText)
        {
            throw new GitException("Файл изменился. Обновите diff и повторите раскрытие контекста.");
        }
    }

    private async Task<FileDiff> ReadUntrackedAsync(string worktree, string relativePath, bool isGitLink)
    {
        var root = Path.GetFullPath(worktree);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Путь к файлу выходит за пределы рабочей копии.");
        }

        if (Directory.Exists(path))
        {
            var hasRepository = File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"));

            if (isGitLink || hasRepository)
            {
                var nestedRepositoryRoot = await this._git.GetRepositoryRootAsync(path);
                if (!GitService.PathsEqual(nestedRepositoryRoot, path))
                {
                    return GitChangeService.EmptyDiff("Каталог не является корнем вложенного Git-репозитория.");
                }

                string hash;
                try
                {
                    hash = (await this._git.RunAsync(path, "rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"))
                        .TrimEnd('\r', '\n');
                }
                catch (GitException)
                {
                    return GitChangeService.EmptyDiff("Во вложенном репозитории пока нет коммита. Создайте в нём коммит, чтобы добавить ссылку на его версию.");
                }

                if (hash.Length is not (40 or 64) || hash.Any(character => !Uri.IsHexDigit(character)))
                {
                    return GitChangeService.EmptyDiff("Не удалось прочитать текущий коммит вложенного репозитория.");
                }

                var patch = $"diff --git a/{relativePath} b/{relativePath}\nnew file mode 160000\nindex 0000000..{hash[..7]}\n--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+Subproject commit {hash}";
                var diff = GitChangeService.ParsePatch(patch, false);

                return diff with
                {
                    Message = "В родительский коммит попадёт ссылка на коммит вложенного репозитория. Незакоммиченные файлы внутри него не попадут в этот коммит."
                };
            }

            return GitChangeService.EmptyDiff("Это папка. Выберите отдельный файл внутри неё для просмотра и коммита.");
        }

        if (!File.Exists(path))
        {
            return GitChangeService.EmptyDiff("Неотслеживаемый файл был удалён после обновления списка.");
        }

        var currentDirectory = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentDirectory = Path.Combine(currentDirectory, segment);
            if ((File.GetAttributes(currentDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new GitException("Просмотр файлов через символические ссылки отключён.");
            }
        }

        if (new FileInfo(path).Length > MaximumFileLength)
        {
            return GitChangeService.EmptyDiff("Файл слишком большой для предпросмотра.");
        }

        var bytes = await File.ReadAllBytesAsync(path);

        if (bytes.Contains((byte)0))
        {
            return new FileDiff("", "", "Бинарный файл. Текстовый diff недоступен.", [], true, "Бинарный файл.");
        }

        string text;

        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return GitChangeService.EmptyDiff("Кодировка файла отличается от UTF-8. Откройте файл в IDE для просмотра без искажения текста.");
        }
        if (text.Length == 0)
        {
            return GitChangeService.EmptyDiff("Пустой неотслеживаемый файл.");
        }

        var lines = GitChangeService.SplitLines(text);
        var aligned = lines.Select((line, index) => new DiffLine(null, "", index + 1, line, DiffLineKind.Added)).ToArray();

        return new FileDiff("", text, string.Join(Environment.NewLine, aligned.Select(line => $"+ {line.NewText}")), aligned, false, null);
    }

    public static FileDiff ParseUnifiedDiff(string patch, bool isTruncated = false)
    {
        var truncated = isTruncated || patch.Length > GitChangeService.MaximumPatchLength;
        var boundedPatch = truncated ? patch[..GitChangeService.MaximumPatchLength] : patch;

        return GitChangeService.ParsePatch(boundedPatch, truncated);
    }

    private static FileDiff ParsePatch(string patch, bool isTruncated)
    {
        if (patch.Contains("Binary files ", StringComparison.Ordinal) || patch.Contains("GIT binary patch", StringComparison.Ordinal))
        {
            return new FileDiff("", "", patch, [], true, "Бинарный файл.");
        }

        var rows = new List<DiffLine>();
        var oldLines = new List<(int Number, string Text)>();
        var newLines = new List<(int Number, string Text)>();
        var oldNumber = 0;
        var newNumber = 0;
        var inHunk = false;

        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)
                || line.StartsWith("diff --cc ", StringComparison.Ordinal))
            {
                GitChangeService.FlushChanges(oldLines, newLines, rows);
                inHunk = false;
                continue;
            }

            var header = GitChangeService.s_hunkHeader.Match(line.TrimEnd('\r'));
            if (header.Success)
            {
                GitChangeService.FlushChanges(oldLines, newLines, rows);
                oldNumber = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                newNumber = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
                inHunk = true;
                continue;
            }

            if (!inHunk || line.Length == 0)
            {
                continue;
            }

            var text = line.TrimEnd('\r');
            if (text.StartsWith("\\ No newline", StringComparison.Ordinal))
            {
                continue;
            }

            if (text[0] == ' ')
            {
                GitChangeService.FlushChanges(oldLines, newLines, rows);
                rows.Add(new DiffLine(oldNumber++, text[1..], newNumber++, text[1..], DiffLineKind.Equal));
            }
            else if (text[0] == '-')
            {
                oldLines.Add((oldNumber++, text[1..]));
            }
            else if (text[0] == '+')
            {
                newLines.Add((newNumber++, text[1..]));
            }
        }

        GitChangeService.FlushChanges(oldLines, newLines, rows);
        var oldText = string.Join(Environment.NewLine, rows.Where(row => row.OldNumber.HasValue).Select(row => row.OldText));
        var newText = string.Join(Environment.NewLine, rows.Where(row => row.NewNumber.HasValue).Select(row => row.NewText));
        var message = isTruncated
            ? "Diff обрезан: достигнут предел предпросмотра."
            : rows.Count == 0 && patch.Length > 0
                ? "Изменены метаданные файла; содержимое не менялось. Подробности приведены в тексте diff."
                : patch.Length == 0 ? "Для этого файла нет изменений в выбранном слое." : null;

        if (patch.Contains('\uFFFD'))
        {
            message = "В тексте есть символ \uFFFD: он может находиться в исходном файле либо появиться при чтении другой кодировки. Проверьте файл в IDE."
                + (message is null ? "" : "\n" + message);
        }

        return new FileDiff(oldText, newText, patch, rows, false, message);
    }

    private static void FlushChanges(List<(int Number, string Text)> removed, List<(int Number, string Text)> added, List<DiffLine> rows)
    {
        var paired = Math.Min(removed.Count, added.Count);
        for (var index = 0; index < paired; index++)
        {
            rows.Add(new DiffLine(removed[index].Number, removed[index].Text, added[index].Number, added[index].Text, DiffLineKind.Modified));
        }

        for (var index = paired; index < removed.Count; index++)
        {
            rows.Add(new DiffLine(removed[index].Number, removed[index].Text, null, "", DiffLineKind.Removed));
        }

        for (var index = paired; index < added.Count; index++)
        {
            rows.Add(new DiffLine(null, "", added[index].Number, added[index].Text, DiffLineKind.Added));
        }

        removed.Clear();
        added.Clear();
    }

    public static IReadOnlyList<ChangedFile> ParseNameStatus(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<ChangedFile>();
        for (var index = 0; index < fields.Length; index++)
        {
            var status = fields[index];
            if (status.StartsWith('R') || status.StartsWith('C'))
            {
                if (index + 2 >= fields.Length)
                {
                    break;
                }

                var oldPath = fields[++index];
                var newPath = fields[++index];
                files.Add(new ChangedFile(status, newPath, oldPath));
                continue;
            }

            if (index + 1 < fields.Length)
            {
                files.Add(new ChangedFile(status, fields[++index], null));
            }
        }

        return files;
    }

    private static bool IsConflict(string status) => status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";

    private static IReadOnlyList<CommitInfo> ParseCommits(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('\u001f', 5))
        .Where(parts => parts.Length == 5)
        .Select(parts => new CommitInfo(parts[1], parts[4], parts[2], parts[3]))
        .ToArray();

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n') is { Length: > 0 } lines && lines[^1].Length == 0
        ? lines[..^1]
        : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Limit(string text) => text.Length <= MaximumPatchLength
        ? text
        : text[..MaximumPatchLength] + "\n… Показана часть diff.";

    private static FileDiff EmptyDiff(string message) => new("", "", "", [], false, message);
}
