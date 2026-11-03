using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LanLauncher.Client;

/// <summary>Downloads packages via the legacy HTTP endpoint or directly from a NAS share.</summary>
internal static class GamePackageTransfer
{
    private sealed record PackageLocation([property: JsonPropertyName("relativePath")] string RelativePath);

    internal static async Task<string> GetPackageNameAsync(
        HttpClient client, string server, string gameId, CancellationToken token)
    {
        var source = await ResolveSourceAsync(client, server, gameId, token);
        if (source is not null) return Path.GetFileName(source);
        using var response = await client.GetAsync(
            PackageUrl(server, gameId), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? gameId + ".zip";
    }

    internal static async Task<string> DownloadAsync(
        HttpClient client, string server, string gameId, string destination,
        Action<long, long?> progress, CancellationToken token, string? clientId = null)
    {
        var source = await ResolveSourceAsync(client, server, gameId, token);
        if (source is not null)
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, useAsync: true);
            await CopyAsync(input, destination, input.Length, progress, token);
            return Path.GetFileName(source);
        }

        var url = PackageUrl(server, gameId) + (clientId is null ? "" : "?clientId=" + Uri.EscapeDataString(clientId));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? gameId + ".zip";
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await CopyAsync(stream, destination, response.Content.Headers.ContentLength, progress, token);
        return name;
    }

    private static async Task<string?> ResolveSourceAsync(
        HttpClient client, string server, string gameId, CancellationToken token)
    {
        var capabilities = await ServerTransferResolver.DetectAsync(client, server, token);
        if (capabilities.DownloadMode != "smb") return null;
        using var response = await client.GetAsync(
            server.TrimEnd('/') + "/api/games/" + Uri.EscapeDataString(gameId) + "/package-location",
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var location = await response.Content.ReadFromJsonAsync<PackageLocation>(cancellationToken: token)
            ?? throw new InvalidDataException("Der NAS-Server hat keinen Paketpfad geliefert.");
        var path = ServerTransferResolver.ResolveSmbPackagePath(capabilities, location.RelativePath);
        if (!File.Exists(path)) throw new FileNotFoundException(
            "NAS-Paket nicht erreichbar. Bitte SMB-Leserechte und NAS-Verbindung prüfen.", path);
        return path;
    }

    private static string PackageUrl(string server, string gameId) =>
        server.TrimEnd('/') + "/api/games/" + Uri.EscapeDataString(gameId) + "/package";

    private static async Task CopyAsync(Stream input, string destination, long? total,
        Action<long, long?> progress, CancellationToken token)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            received += count;
            progress(received, total);
        }
        await output.FlushAsync(token);
        if (total.HasValue && received != total.Value)
            throw new IOException("Die Paketübertragung ist unvollständig.");
    }
}
