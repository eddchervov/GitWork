using System.IO;
using System.Security.Cryptography;
using GitWork.App;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace GitWork.UiChecks;

internal static class LiveUpdateChecks
{
    public static async Task RunAsync()
    {
        var result = await new UpdateService().CheckAsync();
        if (result.Status is not (UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable)
            || result.LatestVersion is null || result.InstallerUrl is null)
        {
            throw new InvalidOperationException("Не удалось проверить публичный источник обновлений: " + result.ErrorMessage);
        }

        Console.WriteLine($"OK: публичная проверка обновлений: {result.Status}, версия {result.LatestVersion}.");
        var source = new SimpleWebSource(UpdateService.GetVersionedReleaseBaseUri(result.LatestVersion), null, 2);
        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, "GitWork", "win-x64-stable", null, null);
        var asset = feed.Assets.Single(item => item.PackageId == "GitWork" && item.Type == VelopackAssetType.Full
            && item.Version.ToString() == result.LatestVersion);
        if (asset.FileName != $"GitWork-{result.LatestVersion}-win-x64-stable-full.nupkg"
            || asset.SHA256 is not { Length: 64 } || !asset.SHA256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Некорректное имя или контрольная сумма публичного пакета.");
        }

        var root = Path.GetFullPath("artifacts/update-smoke");
        var package = Path.GetFullPath(Path.Combine(root, asset.FileName));
        if (!package.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Путь тестового пакета выходит за пределы каталога проверки.");
        }

        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await source.DownloadReleaseEntry(NullVelopackLogger.Instance, asset, package, _ => { }, cancellation.Token);
            await using var stream = File.OpenRead(package);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (stream.Length != asset.Size || !hash.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Загруженный пакет не совпадает с опубликованной контрольной суммой.");
            }

            Console.WriteLine($"OK: Velopack загрузил пакет версии {result.LatestVersion}, {stream.Length} байт; SHA-256 совпадает. Установка не запускалась.");
        }
        finally
        {
            File.Delete(package);
        }
    }
}
