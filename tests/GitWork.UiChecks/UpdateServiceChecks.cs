using System.Net;
using System.Net.Http;
using System.Text;
using GitWork.App;

namespace GitWork.UiChecks;

internal static class UpdateServiceChecks
{
    public static void Run()
    {
        UpdateServiceChecks.ChecksMissingRepository();
        UpdateServiceChecks.ChecksUnpublishedRelease();
        UpdateServiceChecks.ChecksCurrentVersionAndInstaller();
        UpdateServiceChecks.ChecksAvailableVersionAndRejectsForeignInstaller();
        UpdateServiceChecks.ChecksNetworkFailure();
    }

    private static void ChecksMissingRepository()
    {
        using var client = UpdateServiceChecks.CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.NoReleaseSource, "Репозиторий 404 должен отличаться от актуальной версии.");
    }

    private static void ChecksUnpublishedRelease()
    {
        using var client = UpdateServiceChecks.CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : UpdateServiceChecks.JsonResponse("{}"));
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.NotPublished, "Доступный репозиторий без релиза должен иметь отдельное состояние.");
    }

    private static void ChecksCurrentVersionAndInstaller()
    {
        var version = UpdateService.GetVersionForDisplay();
        using var client = UpdateServiceChecks.CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? UpdateServiceChecks.JsonResponse(UpdateServiceChecks.ReleaseJson($"v{version}", UpdateServiceChecks.ValidInstallerUrl($"v{version}")))
            : UpdateServiceChecks.JsonResponse("{}"));
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.UpToDate, "Та же версия должна считаться актуальной.");
        UpdateServiceChecks.Assert(!result.IsInstalled && result.InstallerUrl is not null, "Переносная копия должна получить ссылку на установщик той же версии.");
    }

    private static void ChecksAvailableVersionAndRejectsForeignInstaller()
    {
        using var validClient = UpdateServiceChecks.CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? UpdateServiceChecks.JsonResponse(UpdateServiceChecks.ReleaseJson("v999.0.0", UpdateServiceChecks.ValidInstallerUrl("v999.0.0")))
            : UpdateServiceChecks.JsonResponse("{}"));
        var validResult = new UpdateService(validClient).CheckAsync().GetAwaiter().GetResult();
        UpdateServiceChecks.Assert(validResult.Status == UpdateCheckStatus.UpdateAvailable, "Новая версия должна отображаться как доступное обновление.");
        UpdateServiceChecks.Assert(validResult.InstallerUrl is not null, "Установщик Git Work из релиза должен быть доступен.");

        using var foreignClient = UpdateServiceChecks.CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
            ? UpdateServiceChecks.JsonResponse(UpdateServiceChecks.ReleaseJson("v999.0.0", "https://github.com/other/project/releases/download/v999.0.0/OtherSetup.exe"))
            : UpdateServiceChecks.JsonResponse("{}"));
        var foreignResult = new UpdateService(foreignClient).CheckAsync().GetAwaiter().GetResult();
        UpdateServiceChecks.Assert(foreignResult.InstallerUrl is null, "Ссылка на установщик чужого репозитория должна отклоняться.");
    }

    private static void ChecksNetworkFailure()
    {
        using var client = UpdateServiceChecks.CreateClient(_ => throw new HttpRequestException("test failure"));
        var result = new UpdateService(client).CheckAsync().GetAwaiter().GetResult();

        UpdateServiceChecks.Assert(result.Status == UpdateCheckStatus.Unavailable, "Сетевая ошибка не должна отображаться как успешная проверка.");
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new StubHttpMessageHandler(handler));
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static string ReleaseJson(string tagName, string installerUrl)
    {
        return $$"""
            {
              "tag_name": "{{tagName}}",
              "body": "Test release",
              "assets": [
                {
                  "name": "GitWork-win-Setup.exe",
                  "browser_download_url": "{{installerUrl}}"
                }
              ]
            }
            """;
    }

    private static string ValidInstallerUrl(string tagName)
    {
        return $"https://github.com/eddchervov/GitWork/releases/download/{tagName}/GitWork-win-Setup.exe";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
