using System.Text;

namespace GitWork.Core;

internal static class DiffContextLoader
{
    private const int MaximumTextLength = 1_000_000;
    private const int MaximumLineCount = 50_000;

    public static bool CanLoad(FileDiff diff) => !diff.IsBinary
        && diff.Message?.Contains("Diff обрезан: достигнут предел предпросмотра.", StringComparison.Ordinal) != true;

    public static async Task<DiffContext> ReadAsync(
        GitService git,
        string repository,
        string? oldRevision,
        string oldPath,
        string? newRevision,
        string newPath)
    {
        var oldText = oldRevision is null ? "" : await DiffContextLoader.ReadBlobAsync(git, repository, oldRevision, oldPath);
        var newText = newRevision is null ? "" : await DiffContextLoader.ReadBlobAsync(git, repository, newRevision, newPath);

        return new DiffContext(DiffContextLoader.SplitLines(oldText), DiffContextLoader.SplitLines(newText));
    }

    public static async Task<DiffContext> ReadIndexAndFileAsync(
        GitService git,
        string worktree,
        string oldPath,
        string newPath)
    {
        var indexText = await DiffContextLoader.ReadIndexBlobAsync(git, worktree, oldPath);
        var fileText = await DiffContextLoader.ReadFileAsync(worktree, newPath);

        return new DiffContext(DiffContextLoader.SplitLines(indexText), DiffContextLoader.SplitLines(fileText));
    }

    public static async Task<DiffContext> ReadHeadAndIndexAsync(GitService git, string worktree, string oldPath, string newPath)
    {
        var head = await DiffContextLoader.ResolveHeadAsync(git, worktree);
        var oldText = head is null ? "" : await DiffContextLoader.ReadBlobAsync(git, worktree, head, oldPath);
        var newText = await DiffContextLoader.ReadIndexBlobAsync(git, worktree, newPath);

        return new DiffContext(DiffContextLoader.SplitLines(oldText), DiffContextLoader.SplitLines(newText));
    }

    public static async Task<DiffContext> ReadRevisionAndFileAsync(
        GitService git,
        string repository,
        string revision,
        string oldPath,
        string worktree,
        string newPath)
    {
        var oldText = await DiffContextLoader.ReadBlobAsync(git, repository, revision, oldPath);
        var newText = await DiffContextLoader.ReadFileAsync(worktree, newPath);

        return new DiffContext(DiffContextLoader.SplitLines(oldText), DiffContextLoader.SplitLines(newText));
    }

    public static async Task<string?> ResolveHeadAsync(GitService git, string worktree)
    {
        try
        {
            return (await git.RunAsync(worktree, "rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"))
                .TrimEnd('\r', '\n');
        }
        catch (GitException)
        {
            return null;
        }
    }

    public static async Task<string> ReadFileAsync(string worktree, string relativePath)
    {
        var root = Path.GetFullPath(worktree);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GitException("Просмотр файлов через символические ссылки отключён.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitException("Путь к файлу выходит за пределы рабочей копии.");
        }

        var currentPath = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                return "";
            }

            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new GitException("Просмотр файлов через символические ссылки отключён.");
            }
        }

        if (!File.Exists(path))
        {
            return "";
        }

        if (new FileInfo(path).Length > DiffContextLoader.MaximumTextLength)
        {
            throw new GitException("Файл слишком большой для раскрытия контекста.");
        }

        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length > DiffContextLoader.MaximumTextLength)
        {
            throw new GitException("Файл слишком большой для раскрытия контекста.");
        }

        if (bytes.Contains((byte)0))
        {
            throw new GitException("Контекст бинарного файла нельзя показать как текст.");
        }

        return DiffContextLoader.Decode(bytes);
    }

    private static async Task<string> ReadBlobAsync(GitService git, string repository, string revision, string path)
    {
        var treeEntry = await git.RunLimitedAsync(repository, 10_000,
            "ls-tree", "-z", "--full-tree", revision, "--", ":(literal)" + path);
        if (treeEntry.Length == 0)
        {
            return "";
        }

        var separator = treeEntry.IndexOf('\t');
        if (separator < 0)
        {
            throw new GitException("Git вернул неверный путь к версии файла.");
        }

        var fields = treeEntry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[1] != "blob")
        {
            return "";
        }

        return await DiffContextLoader.ReadBlobByHashAsync(git, repository, fields[2]);
    }

    private static async Task<string> ReadIndexBlobAsync(GitService git, string worktree, string path)
    {
        var entries = await git.RunLimitedAsync(worktree, 10_000,
            "ls-files", "--stage", "-z", "--", ":(literal)" + path);
        foreach (var entry in entries.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\t');
            if (separator < 0)
            {
                continue;
            }

            var fields = entry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && fields[2] == "0")
            {
                return await DiffContextLoader.ReadBlobByHashAsync(git, worktree, fields[1]);
            }
        }

        return "";
    }

    private static async Task<string> ReadBlobByHashAsync(GitService git, string repository, string hash)
    {
        var sizeText = (await git.RunAsync(repository, "cat-file", "-s", hash)).TrimEnd('\r', '\n');
        if (!int.TryParse(sizeText, out var size) || size > DiffContextLoader.MaximumTextLength)
        {
            throw new GitException("Файл слишком большой для раскрытия контекста.");
        }

        var output = await git.RunLimitedAsync(repository, DiffContextLoader.MaximumTextLength + 1,
            "cat-file", "blob", hash);
        if (output.Length > DiffContextLoader.MaximumTextLength || output.Contains('\0'))
        {
            throw new GitException("Файл слишком большой или бинарный для раскрытия контекста.");
        }

        if (output.Contains('\uFFFD'))
        {
            throw new GitException("Кодировка файла не поддерживается для раскрытия контекста.");
        }

        var outputByteCount = Encoding.UTF8.GetByteCount(output);
        if (outputByteCount + 3 == size)
        {
            output = "\uFEFF" + output;
        }
        else if (outputByteCount != size)
        {
            throw new GitException("Кодировка файла не поддерживается для раскрытия контекста.");
        }

        return output;
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Contains('\uFFFD'))
            {
                throw new GitException("Кодировка файла не поддерживается для раскрытия контекста.");
            }

            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new GitException("Кодировка файла не поддерживается для раскрытия контекста.");
        }
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0)
        {
            Array.Resize(ref lines, lines.Length - 1);
        }

        if (lines.Length > DiffContextLoader.MaximumLineCount)
        {
            throw new GitException("В файле слишком много строк для раскрытия контекста.");
        }

        return lines;
    }
}
