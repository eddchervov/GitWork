using GitWork.Core;
using System.Text;
using System.Net;
using System.Net.Sockets;

internal static class CloneChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        GitCloneService.ValidateUrl("https://github.com/owner/repository.git");
        check(true, "HTTPS URL клонирования принимается");
        GitCloneService.ValidateUrl("http://tfs.example.invalid/collection/_git/repository");
        check(true, "HTTP URL внутреннего Git-сервера принимается");
        GitCloneService.ValidateUrl("ssh://git@github.com/owner/repository.git");
        check(true, "SSH URL клонирования принимается");
        GitCloneService.ValidateUrl("git@github.com:owner/repository.git");
        check(true, "SSH SCP адрес клонирования принимается");

        var rejectedUrls = new[]
        {
            "-c core.sshCommand=unsafe",
            "file:///C:/private/repository",
            "https://user:password@example.com/owner/repository.git",
            "https://example.com/",
            "ext::cmd /c unsafe",
            "git@-oProxyCommand:repository.git",
            "https://example.com/owner/\nrepository.git"
        };

        foreach (var url in rejectedUrls)
        {
            var rejected = false;

            try
            {
                GitCloneService.ValidateUrl(url);
            }
            catch (GitException)
            {
                rejected = true;
            }

            check(rejected, $"Небезопасный или неполный URL отклоняется: {url}");
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"GitWork-clone-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var newDestination = Path.Combine(temporaryRoot, "new-repository");
            GitCloneService.ValidateDestination(newDestination);
            check(true, "Свободная папка внутри существующей родительской папки принимается");

            var existingDestination = Path.Combine(temporaryRoot, "existing-repository");
            Directory.CreateDirectory(existingDestination);
            var markerPath = Path.Combine(existingDestination, "keep.txt");
            File.WriteAllText(markerPath, "Сохранить");
            var rejectedExistingDestination = false;

            try
            {
                GitCloneService.ValidateDestination(existingDestination);
            }
            catch (GitException)
            {
                rejectedExistingDestination = true;
            }

            check(rejectedExistingDestination && File.ReadAllText(markerPath) == "Сохранить", "Существующая папка отклоняется без изменения её файлов");

            var missingParentDestination = Path.Combine(temporaryRoot, "missing", "new-repository");
            var rejectedMissingParent = false;

            try
            {
                GitCloneService.ValidateDestination(missingParentDestination);
            }
            catch (GitException)
            {
                rejectedMissingParent = true;
            }

            check(rejectedMissingParent, "Несуществующая родительская папка отклоняется");
            await CloneChecks.CheckLocalServerAsync(temporaryRoot, check);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(temporaryRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(temporaryRoot, true);
        }
    }

    private static async Task CheckLocalServerAsync(string root, Action<bool, string> check)
    {
        var git = new GitService();
        var seed = Path.Combine(root, "seed");
        Directory.CreateDirectory(seed);
        await git.RunAsync(seed, "init", "--initial-branch=main");
        await git.RunAsync(seed, "config", "user.name", "Git Work Clone Test");
        await git.RunAsync(seed, "config", "user.email", "clone@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "test clone\n");
        await git.RunAsync(seed, "add", "--", "file.txt");
        await git.RunAsync(seed, "commit", "-m", "test clone");
        var serverRoot = Path.Combine(root, "server");
        var bare = Path.Combine(serverRoot, "repository.git");
        Directory.CreateDirectory(serverRoot);
        await git.RunAsync(root, "clone", "--bare", "--", seed, bare);
        await git.RunAsync(bare, "update-server-info");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var server = CloneChecks.ServeRepositoryAsync(listener, serverRoot, cancellation.Token);
        try
        {
            var destination = Path.Combine(root, "cloned-from-url");
            await new GitCloneService(git).CloneAsync($"http://127.0.0.1:{port}/repository.git", destination);
            check((await File.ReadAllTextAsync(Path.Combine(destination, "file.txt"))).Replace("\r\n", "\n") == "test clone\n"
                && GitService.PathsEqual(await git.GetRepositoryRootAsync(destination), destination),
                "Клонирование по HTTP URL с локального тестового сервера создаёт доступный репозиторий");
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            await server;
        }
    }

    private static async Task ServeRepositoryAsync(TcpListener listener, string root, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var request = await reader.ReadLineAsync(cancellationToken);
                if (request is null)
                {
                    continue;
                }

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                }

                var relative = Uri.UnescapeDataString(request.Split(' ')[1].Split('?')[0].TrimStart('/'));
                var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                var found = path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
                var content = found ? await File.ReadAllBytesAsync(path, cancellationToken) : Array.Empty<byte>();
                var response = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/plain\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
                await stream.WriteAsync(content, cancellationToken);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }
}
