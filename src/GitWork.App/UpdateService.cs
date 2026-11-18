using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace GitWork.App;

public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/eddchervov/GitWork";
    private const string LatestFeedUrl = "https://github.com/eddchervov/GitWork/releases/latest/download/releases.win-x64-stable.json";
    private const string Channel = "win-x64-stable";
    private static readonly HttpClient s_httpClient = UpdateService.CreateHttpClient();
    private readonly HttpClient _httpClient;

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
            if (VelopackLocator.IsCurrentSet)
            {
                isInstalled = this.GetUpdateManager(currentVersion, timeoutMinutes: 0.25).IsInstalled;
            }

            using var response = await this._httpClient.GetAsync(UpdateService.LatestFeedUrl, cancellationToken);
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
            using var feed = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var release = UpdateService.ReadLatestRelease(feed.RootElement);
            if (release is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    currentVersion,
                    ErrorMessage: "В release feed нет подходящего полного стабильного пакета Git Work.",
                    IsInstalled: isInstalled);
            }

            var latestVersion = release.Version;
            var latestVersionText = latestVersion.ToString();
            var releaseNotes = release.NotesMarkdown;
            var installerUrl = UpdateService.GetInstallerUrl(latestVersionText);
            var parsedCurrentVersion = SemanticVersion.Parse(currentVersion);
            if (latestVersion.CompareTo(parsedCurrentVersion) <= 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    currentVersion,
                    latestVersionText,
                    releaseNotes,
                    installerUrl,
                    IsInstalled: isInstalled);
            }

            UpdateInfo? updateInfo = null;
            if (isInstalled)
            {
                var updateManager = this.GetUpdateManager(latestVersionText, timeoutMinutes: 0.25);
                updateInfo = await updateManager.CheckForUpdatesAsync();
                if (updateInfo is null
                    || updateInfo.TargetFullRelease.Version.CompareTo(latestVersion) != 0
                    || !UpdateService.MatchesReleaseAsset(updateInfo.TargetFullRelease, release))
                {
                    return new UpdateCheckResult(
                        UpdateCheckStatus.Unavailable,
                        currentVersion,
                        latestVersionText,
                        releaseNotes,
                        installerUrl,
                        ErrorMessage: "Velopack не смог проверить пакеты этой версии в закреплённом release feed.",
                        IsInstalled: true);
                }
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                currentVersion,
                latestVersionText,
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
        if (!result.HasUpdate || result.UpdateInfo is null || result.LatestVersion is null
            || !UpdateService.TryParseStableVersion(result.LatestVersion, out var latestVersion)
            || result.UpdateInfo.TargetFullRelease.Version.CompareTo(latestVersion) != 0
            || !UpdateService.IsExpectedPackageAsset(result.UpdateInfo.TargetFullRelease, latestVersion))
        {
            throw new InvalidOperationException("Для этой копии приложения обновление через Velopack недоступно.");
        }

        var updateManager = this.GetUpdateManager(latestVersion.ToString(), timeoutMinutes: 15);
        if (updateManager.IsInstalled != true)
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

    public static Uri GetVersionedReleaseBaseUri(string version)
    {
        if (!UpdateService.TryParseStableVersion(version, out var parsedVersion)
            || !string.Equals(parsedVersion.ToString(), version, StringComparison.Ordinal))
        {
            throw new ArgumentException("Версия должна состоять из трёх числовых частей.", nameof(version));
        }

        return new Uri($"{UpdateService.RepositoryUrl}/releases/download/v{version}/", UriKind.Absolute);
    }

    public static string GetVersionForDisplay()
    {
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 1, 0);

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitWork", UpdateService.GetVersionForDisplay()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    private UpdateManager GetUpdateManager(string version, double timeoutMinutes)
    {
        if (!VelopackLocator.IsCurrentSet)
        {
            throw new InvalidOperationException("Эта копия приложения не установлена через Velopack.");
        }

        var baseUri = UpdateService.GetVersionedReleaseBaseUri(version);
        var source = new SimpleWebSource(baseUri, null, timeoutMinutes);

        return new UpdateManager(source, new UpdateOptions { ExplicitChannel = UpdateService.Channel });
    }

    private static ReleaseAsset? ReadLatestRelease(JsonElement feed)
    {
        if (feed.ValueKind != JsonValueKind.Object
            || !feed.TryGetProperty("Assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ReleaseAsset? latest = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var candidate = UpdateService.ReadReleaseAsset(asset);
            if (candidate is not null && (latest is null || candidate.Version.CompareTo(latest.Version) > 0))
            {
                latest = candidate;
            }
        }

        return latest;
    }

    private static ReleaseAsset? ReadReleaseAsset(JsonElement asset)
    {
        if (asset.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var packageId = UpdateService.ReadString(asset, "PackageId");
        var versionText = UpdateService.ReadString(asset, "Version");
        var assetType = UpdateService.ReadString(asset, "Type");
        var fileName = UpdateService.ReadString(asset, "FileName");
        var sha1 = UpdateService.ReadString(asset, "SHA1");
        var sha256 = UpdateService.ReadString(asset, "SHA256");
        if (packageId != "GitWork" || assetType != "Full"
            || !UpdateService.TryParseStableVersion(versionText, out var version)
            || fileName != $"GitWork-{version}-win-x64-stable-full.nupkg"
            || !UpdateService.IsHexDigest(sha1, 40) || !UpdateService.IsHexDigest(sha256, 64)
            || !asset.TryGetProperty("Size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size) || size <= 0)
        {
            return null;
        }

        return new ReleaseAsset(version, fileName, sha1, sha256, size, UpdateService.ReadString(asset, "NotesMarkdown"));
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryParseStableVersion(string value, out SemanticVersion version)
    {
        version = default!;
        if (!Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)
            || !SemanticVersion.TryParse(value, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static bool IsHexDigest(string digest, int expectedLength) =>
        digest.Length == expectedLength && digest.All(Uri.IsHexDigit);

    private static Uri GetInstallerUrl(string version) =>
        new($"{UpdateService.RepositoryUrl}/releases/download/v{version}/GitWork-win-x64-stable-Setup.exe", UriKind.Absolute);

    private static bool MatchesReleaseAsset(VelopackAsset asset, ReleaseAsset release) =>
        UpdateService.IsExpectedPackageAsset(asset, release.Version)
        && string.Equals(asset.SHA1, release.Sha1, StringComparison.OrdinalIgnoreCase)
        && string.Equals(asset.SHA256, release.Sha256, StringComparison.OrdinalIgnoreCase)
        && asset.Size == release.Size;

    private static bool IsExpectedPackageAsset(VelopackAsset asset, SemanticVersion version) =>
        asset.PackageId == "GitWork"
        && asset.Type.ToString() == "Full"
        && asset.Version.CompareTo(version) == 0
        && asset.FileName == $"GitWork-{version}-win-x64-stable-full.nupkg"
        && UpdateService.IsHexDigest(asset.SHA1, 40)
        && UpdateService.IsHexDigest(asset.SHA256, 64)
        && asset.Size > 0;

    private sealed record ReleaseAsset(
        SemanticVersion Version,
        string FileName,
        string Sha1,
        string Sha256,
        long Size,
        string NotesMarkdown);
}
