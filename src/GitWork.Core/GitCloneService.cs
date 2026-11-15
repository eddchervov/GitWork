using System.Text.RegularExpressions;

namespace GitWork.Core;

public sealed class GitCloneService(GitService git)
{
    private static readonly Regex s_scpUrlPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*:[^\r\n]+$", RegexOptions.CultureInvariant);
    private readonly GitService _git = git;

    public async Task CloneAsync(string url, string destination, IProgress<string>? progress = null)
    {
        GitCloneService.ValidateUrl(url);
        GitCloneService.ValidateDestination(destination);
        var fullDestination = Path.GetFullPath(destination);
        progress?.Report("Клонирование репозитория…");

        try
        {
            await this._git.RunWithTimeoutAsync(
                Path.GetDirectoryName(fullDestination)!,
                TimeSpan.FromMinutes(30),
                "clone",
                "--progress",
                "--",
                url.Trim(),
                fullDestination);
        }
        catch (GitException exception)
        {
            if (Directory.Exists(fullDestination) || File.Exists(fullDestination))
            {
                throw new GitException(
                    $"Клонирование не завершилось. Папка сохранена: {fullDestination}. "
                    + $"Проверьте её и повторите попытку в другой папке. {exception.Message}");
            }

            throw;
        }

        progress?.Report("Проверка репозитория…");
        var repositoryRoot = await this._git.GetRepositoryRootAsync(fullDestination);

        if (!GitService.PathsEqual(repositoryRoot, fullDestination))
        {
            throw new GitException("Git создал репозиторий не в выбранной папке. Папка сохранена, проверьте её перед повтором.");
        }

        progress?.Report("Репозиторий готов.");
    }

    public static void ValidateUrl(string url)
    {
        var value = url?.Trim();

        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new GitException("Укажите URL репозитория.");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme is "http" or "https" or "ssh" or "git"))
        {
            if (!string.IsNullOrWhiteSpace(uri.Host)
                && uri.AbsolutePath.Length > 1
                && (string.IsNullOrEmpty(uri.UserInfo)
                    || uri.Scheme == "ssh" && !uri.UserInfo.Contains(':')))
            {
                return;
            }

            throw new GitException("Поддерживаются HTTP, HTTPS, SSH и Git URL без пароля в адресе.");
        }

        if (GitCloneService.s_scpUrlPattern.IsMatch(value))
        {
            return;
        }

        throw new GitException("Укажите URL Git-репозитория или SSH-адрес вида git@host:owner/repository.git.");
    }

    public static void ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
        {
            throw new GitException("Укажите полный путь новой папки репозитория.");
        }

        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination);
        var folderName = Path.GetFileName(fullDestination);

        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(folderName)
            || !Directory.Exists(parent))
        {
            throw new GitException("Выберите существующую родительскую папку.");
        }

        if (Directory.Exists(fullDestination) || File.Exists(fullDestination))
        {
            throw new GitException("Папка назначения уже существует. Выберите другое имя.");
        }
    }
}
