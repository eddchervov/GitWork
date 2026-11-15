using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace GitWork.App;

public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/eddchervov/GitWork";
    private const string RepositoryApiUrl = "https://api.github.com/repos/eddchervov/GitWork";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/eddchervov/GitWork/releases/latest";
    private const string Channel = "win-x64-stable";
    private static readonly HttpClient s_httpClient = UpdateService.CreateHttpClient();
    private readonly HttpClient _httpClient;
    private UpdateManager? _updateManager;

    public UpdateService(HttpClient? httpClient = null)
    {
        this._httpClient = httpClient ?? UpdateService.s_httpClient;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = UpdateService.GetVersionForDisplay();
        var isInstalled = false;

        try
        {
            var updateManager = this.GetUpdateManager();
            isInstalled = updateManager?.IsInstalled == true;
            using var repositoryResponse = await this._httpClient.GetAsync(UpdateService.RepositoryApiUrl, cancellationToken);
            if (repositoryResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NoReleaseSource, currentVersion, IsInstalled: isInstalled);
            }

            if (!repositoryResponse.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    currentVersion,
                    ErrorMessage: $"GitHub вернул HTTP {(int)repositoryResponse.StatusCode}.",
                    IsInstalled: isInstalled);
            }

            using var response = await this._httpClient.GetAsync(UpdateService.LatestReleaseApiUrl, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NotPublished, currentVersion, IsInstalled: isInstalled);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    currentVersion,
                    ErrorMessage: $"GitHub вернул HTTP {(int)response.StatusCode}.",
                    IsInstalled: isInstalled);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var release = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = release.RootElement;
            var tagName = UpdateService.ReadString(root, "tag_name");
            var version = tagName.TrimStart('v', 'V');
            if (!SemanticVersion.TryParse(version, out var latestVersion))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    currentVersion,
                    ErrorMessage: "У релиза некорректная версия.",
                    IsInstalled: isInstalled);
            }

            var parsedCurrentVersion = SemanticVersion.Parse(currentVersion);
            var releaseNotes = UpdateService.ReadString(root, "body");
            var installerUrl = UpdateService.ReadInstallerUrl(root, tagName);
            if (latestVersion.CompareTo(parsedCurrentVersion) <= 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    currentVersion,
                    latestVersion.ToString(),
                    releaseNotes,
                    installerUrl,
                    IsInstalled: isInstalled);
            }

            UpdateInfo? updateInfo = null;
            if (isInstalled)
            {
                updateInfo = await updateManager!.CheckForUpdatesAsync();
                if (updateInfo is null)
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.Unavailable,
                        currentVersion,
                        latestVersion.ToString(),
                        releaseNotes,
                        installerUrl,
                        ErrorMessage: "В релизе пока нет пакета обновления для этого канала.",
                        IsInstalled: true);
                }
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                currentVersion,
                latestVersion.ToString(),
                releaseNotes,
                installerUrl,
                updateInfo,
                IsInstalled: isInstalled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Unavailable,
                currentVersion,
                ErrorMessage: "Проверка обновлений превысила время ожидания.",
                IsInstalled: isInstalled);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Unavailable,
                currentVersion,
                ErrorMessage: exception.Message,
                IsInstalled: isInstalled);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);

            return new UpdateCheckResult(
                UpdateCheckStatus.Unavailable,
                currentVersion,
                ErrorMessage: exception.Message,
                IsInstalled: isInstalled);
        }
    }

    public async Task DownloadAndApplyAsync(
        UpdateCheckResult result,
        Func<bool> mayExit,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mayExit);
        var updateManager = this.GetUpdateManager();
        if (!result.HasUpdate || result.UpdateInfo is null || updateManager?.IsInstalled != true)
        {
            throw new InvalidOperationException("Для этой копии приложения обновление через Velopack недоступно.");
        }

        await updateManager.DownloadUpdatesAsync(
            result.UpdateInfo,
            percentage => progress?.Report(percentage),
            cancellationToken);

        if (!mayExit())
        {
            return;
        }

        updateManager.ApplyUpdatesAndRestart(result.UpdateInfo.TargetFullRelease);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitWork", UpdateService.GetVersionForDisplay()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    private UpdateManager? GetUpdateManager()
    {
        if (!VelopackLocator.IsCurrentSet)
        {
            return null;
        }

        return this._updateManager ??= new UpdateManager(
            new GithubSource(UpdateService.RepositoryUrl, accessToken: null, prerelease: false),
            new UpdateOptions { ExplicitChannel = UpdateService.Channel });
    }

    public static string GetVersionForDisplay()
    {
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 1, 0);

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static Uri? ReadInstallerUrl(JsonElement release, string tagName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = UpdateService.ReadString(asset, "name");
            var downloadUrl = UpdateService.ReadString(asset, "browser_download_url");
            if (!name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !UpdateService.IsGitWorkInstallerUrl(uri, tagName, name))
            {
                continue;
            }

            return uri;
        }

        return null;
    }

    private static bool IsGitWorkInstallerUrl(Uri uri, string tagName, string assetName)
    {
        var segments = Uri.UnescapeDataString(uri.AbsolutePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 6
            && segments[0].Equals("eddchervov", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("GitWork", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            && segments[3].Equals("download", StringComparison.OrdinalIgnoreCase)
            && segments[4].Equals(tagName, StringComparison.Ordinal)
            && segments[5].Equals(assetName, StringComparison.OrdinalIgnoreCase);
    }
}
