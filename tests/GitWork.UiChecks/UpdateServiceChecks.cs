using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitWork.App;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace GitWork.UiChecks;

internal static class UpdateServiceChecks
{
    private const string LatestFeedUrl = "https://github.com/eddchervov/GitWork/releases/latest/download/releases.win-x64-stable.json";

    public static void Run()
    {
        UpdateServiceChecks.ChecksNotPublishedFeed();
        UpdateServiceChecks.ChecksUnavailableHttpStatus();
        UpdateServiceChecks.ChecksCurrentVersionAndDerivedInstaller();
        UpdateServiceChecks.ChecksSelectsHighestValidStablePackage();
        UpdateServiceChecks.ChecksRejectsInvalidFeeds();
        UpdateServiceChecks.ChecksNetworkFailure();
        UpdateServiceChecks.ChecksVersionPinnedVelopackUrls();
    }

    private static void ChecksNotPublishedFeed()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.NotPublished
            && handler.Requests.Count == 1 && handler.Requests[0].AbsoluteUri == UpdateServiceChecks.LatestFeedUrl,
            "Feed 404 имеет статус «не опубликовано» и выполняет ровно один GET без API-запроса");
    }

    private static void ChecksUnavailableHttpStatus()
    {
        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.Unavailable
            && result.ErrorMessage?.Contains("403", StringComparison.Ordinal) == true
            && handler.Requests.Count == 1,
            "HTTP 403 отображается как временная ошибка feed без GitHub API");
    }

    private static void ChecksCurrentVersionAndDerivedInstaller()
    {
        var version = UpdateService.GetVersionForDisplay();
        using var handler = new StubHttpMessageHandler(_ => UpdateServiceChecks.JsonResponse(
            UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(version, notes: "# Проверка\n\nТекст релиза."))));
        using var client = new HttpClient(handler);
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();
        var expectedInstaller = UpdateServiceChecks.ExpectedInstallerUrl(version);

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.UpToDate
            && result.LatestVersion == version
            && result.ReleaseNotes == "# Проверка\n\nТекст релиза."
            && result.InstallerUrl == expectedInstaller
            && handler.Requests.Count == 1 && handler.Requests[0].AbsoluteUri == UpdateServiceChecks.LatestFeedUrl,
            "Текущая версия берёт Markdown из feed и вычисляет ссылку Setup из проверенной версии");
    }

    private static void ChecksSelectsHighestValidStablePackage()
    {
        var current = Version.Parse(UpdateService.GetVersionForDisplay());
        var lower = $"{current.Major}.{current.Minor}.{current.Build + 1}";
        var higher = $"{current.Major}.{current.Minor}.{current.Build + 2}";
        var feed = UpdateServiceChecks.FeedJson(
            UpdateServiceChecks.Asset(lower),
            UpdateServiceChecks.Asset("999.0.0-preview.1"),
            UpdateServiceChecks.Asset("999.0.0", packageId: "OtherPackage"),
            UpdateServiceChecks.Asset(higher, notes: "Highest valid release"));
        using var handler = new StubHttpMessageHandler(_ => UpdateServiceChecks.JsonResponse(feed));
        using var client = new HttpClient(handler);
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.UpdateAvailable
            && result.LatestVersion == higher
            && result.InstallerUrl == UpdateServiceChecks.ExpectedInstallerUrl(higher)
            && handler.Requests.Count == 1,
            "Выбирается самая высокая стабильная версия полного пакета GitWork, игнорируя prerelease и чужие пакеты");
    }

    private static void ChecksRejectsInvalidFeeds()
    {
        var validVersion = "9.8.7";
        var invalidFeeds = new (string Json, string Name)[]
        {
            ("{", "Повреждённый JSON feed отклоняется"),
            ("{\"Assets\":[]}", "Пустой release feed отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, packageId: "OtherPackage")), "Чужой PackageId отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, type: "Delta")), "Delta-пакет отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, fileName: "GitWork-other-channel-full.nupkg")), "Неподходящее имя package отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, fileName: "../GitWork-9.8.7-win-x64-stable-full.nupkg")), "Путь с traversal в FileName отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, fileName: "https://example.invalid/package.nupkg")), "Абсолютный URL в FileName отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, sha1: "bad")), "Некорректный SHA1 отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, sha256: "bad")), "Некорректный SHA256 отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(validVersion, size: 0)), "Нулевой размер пакета отклоняется"),
            (UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset("9.8.7.1")), "Версия не из трёх частей отклоняется")
        };

        foreach (var (json, name) in invalidFeeds)
        {
            using var handler = new StubHttpMessageHandler(_ => UpdateServiceChecks.JsonResponse(json));
            using var client = new HttpClient(handler);
            var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();
            UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.Unavailable
                && result.InstallerUrl is null && handler.Requests.Count == 1, name);
        }
    }

    private static void ChecksNetworkFailure()
    {
        using var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("test failure"));
        using var client = new HttpClient(handler);
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.Unavailable,
            "Сетевая ошибка не отображается как успешная проверка");
    }

    private static void ChecksVersionPinnedVelopackUrls()
    {
        const string version = "8.7.6";
        var feedJson = UpdateServiceChecks.FeedJson(UpdateServiceChecks.Asset(version));
        using var httpHandler = new StubHttpMessageHandler(_ => UpdateServiceChecks.JsonResponse(feedJson));
        using var client = new HttpClient(httpHandler);
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();
        var downloader = new RecordingFileDownloader(feedJson);
        var releaseBase = UpdateService.GetVersionedReleaseBaseUri(result.LatestVersion!);
        var source = new SimpleWebSource(releaseBase, downloader);
        var assets = source.GetReleaseFeed(NullVelopackLogger.Instance, "GitWork", "win-x64-stable", null, null)
            .GetAwaiter().GetResult().Assets;
        var package = assets.Single(asset => asset.FileName == $"GitWork-{version}-win-x64-stable-full.nupkg");
        var packagePath = Path.Combine(Path.GetTempPath(), "GitWork-update-source-check-" + Guid.NewGuid().ToString("N") + ".nupkg");

        try
        {
            source.DownloadReleaseEntry(NullVelopackLogger.Instance, package, packagePath, _ => { }, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }

        UpdateServiceChecks.Assert(result.LatestVersion == version
            && downloader.FeedUrls.Count == 1
            && downloader.FeedUrls[0].StartsWith(releaseBase.AbsoluteUri + "releases.win-x64-stable.json?", StringComparison.Ordinal)
            && downloader.PackageUrls.Count == 1
            && downloader.PackageUrls[0] == releaseBase.AbsoluteUri + package.FileName,
            "Velopack feed и package URL закреплены за одной проверенной версией релиза");
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string FeedJson(params object[] assets) => JsonSerializer.Serialize(new { Assets = assets });

    private static object Asset(
        string version,
        string packageId = "GitWork",
        string? fileName = null,
        string type = "Full",
        string sha1 = "0123456789ABCDEF0123456789ABCDEF01234567",
        string sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
        long size = 12_345,
        string notes = "Test release") => new
        {
            PackageId = packageId,
            Version = version,
            Type = type,
            FileName = fileName ?? $"GitWork-{version}-win-x64-stable-full.nupkg",
            SHA1 = sha1,
            SHA256 = sha256,
            Size = size,
            NotesMarkdown = notes
        };

    private static Uri ExpectedInstallerUrl(string version) =>
        new($"https://github.com/eddchervov/GitWork/releases/download/v{version}/GitWork-win-x64-stable-Setup.exe");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class RecordingFileDownloader(string feedJson) : IFileDownloader
    {
        public List<string> FeedUrls { get; } = [];

        public List<string> PackageUrls { get; } = [];

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            this.FeedUrls.Add(url);
            return Task.FromResult(feedJson);
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout) =>
            Task.FromResult(Array.Empty<byte>());

        public Task DownloadFile(
            string url,
            string localFile,
            Action<int>? progress,
            IDictionary<string, string>? headers,
            double timeout,
            CancellationToken cancelToken)
        {
            this.PackageUrls.Add(url);
            File.WriteAllBytes(localFile, []);
            progress?.Invoke(100);
            return Task.CompletedTask;
        }
    }
}
