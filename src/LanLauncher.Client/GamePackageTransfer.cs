using LanLauncher.Client.Logging;
using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LanLauncher.Client;

/// <summary>Downloads packages via the legacy HTTP endpoint or directly from a NAS share.</summary>
internal static class GamePackageTransfer
{
    private sealed record PackageLocation([property: JsonPropertyName("relativePath")] string RelativePath);

    internal sealed record PreparedPackage(string Server, string GameId, string Name, string? Source, string? Share);

    internal static async Task<PreparedPackage> PrepareAsync(HttpClient client, string server, string gameId,
        CancellationToken token, Action<string>? status = null, bool resolveHttpName = false)
    {
        ReportStatus(status, "HTTP: Übertragungsmodus vom Launcher-Server abfragen …");
        var capabilities = await ServerTransferResolver.DetectAsync(client, server, token, status);
        if (capabilities.DownloadMode == "smb")
        {
            ReportStatus(status, "HTTP: NAS-Paketpfad vom Launcher-Server abfragen …");
            using var locationResponse = await NasNetworkOperations.MetadataAsync(client,
                server.TrimEnd('/') + "/api/games/" + Uri.EscapeDataString(gameId) + "/package-location", token, status);
            locationResponse.EnsureSuccessStatusCode();
            var location = await locationResponse.Content.ReadFromJsonAsync<PackageLocation>(cancellationToken: token)
                ?? throw new InvalidDataException("Der NAS-Server hat keinen Paketpfad geliefert.");
            // Pure path validation: no File.Exists or other synchronous SMB calls on the UI thread.
            var source = ServerTransferResolver.ResolveSmbPackagePath(capabilities, location.RelativePath);
            return new(server, gameId, Path.GetFileName(source), source, capabilities.SmbShare);
        }
        var name = gameId + ".zip";
        if (resolveHttpName)
        {
            ReportStatus(status, "HTTP: Paketnamen abfragen …");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(PackageUrl(server, gameId), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            name = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? name;
        }
        return new(server, gameId, name, null, null);
    }

    internal static async Task<string> GetPackageNameAsync(
        HttpClient client, string server, string gameId, CancellationToken token)
    {
        var package = await PrepareAsync(client, server, gameId, token, resolveHttpName: true);
        return package.Name;
    }

    internal static async Task<string> DownloadAsync(
        HttpClient client, string server, string gameId, string destination,
        Action<long, long?> progress, CancellationToken token, string? clientId = null,
        Func<string, Task>? ensureNasConnected = null, Action<string>? status = null,
        Action<bool>? openingWaitChanged = null)
    {
        var package = await PrepareAsync(client, server, gameId, token, status);
        return await DownloadPreparedAsync(client, package, destination, progress, token, clientId, ensureNasConnected, status, openingWaitChanged);
    }

    internal static async Task<string> DownloadPreparedAsync(HttpClient client, PreparedPackage package, string destination,
        Action<long, long?> progress, CancellationToken token, string? clientId = null,
        Func<string, Task>? ensureNasConnected = null, Action<string>? status = null,
        Action<bool>? openingWaitChanged = null)
    {
        token.ThrowIfCancellationRequested();
        if (package.Source is { } source)
        {
            ReportStatus(status, "NAS: vorhandenen Lesezugriff prüfen …");
            if (ensureNasConnected is not null) await ensureNasConnected(package.Share!);
            token.ThrowIfCancellationRequested();
            ReportStatus(status, "NAS: Paketdatei öffnen …");
            var opened = await OpenNasAsync(source, package.Share!, token, status, openingWaitChanged);
            await using var input = opened.Stream;
            // Construction and Length are synchronous even with useAsync:true.
            // Keep the copy off the Dispatcher; never retry after destination creation.
            await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                ReportStatus(status, "NAS: Paket wird gelesen …");
                await CopyAsync(input, destination, opened.Length, progress, token, source, status).ConfigureAwait(false);
            }, token);
            return Path.GetFileName(source);
        }

        ReportStatus(status, "HTTP: Paket herunterladen …");
        var url = PackageUrl(package.Server, package.GameId) + (clientId is null ? "" : "?clientId=" + Uri.EscapeDataString(clientId));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? package.GameId + ".zip";
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await CopyAsync(stream, destination, response.Content.Headers.ContentLength, progress, token, status: status);
        return name;
    }

    private static async Task<(FileStream Stream, long Length)> OpenNasAsync(string source, string share,
        CancellationToken token, Action<string>? status, Action<bool>? openingWaitChanged)
    {
        using var preparation = await NasPreparation.AcquireAsync(share, admin: false, "Paket öffnen", token, status);
        try
        {
            return await preparation.RunOpeningAsync(() =>
            {
                preparation.Report("SMB-Dateiparameter: " + NasPackageFile.Parameters);
                var stream = preparation.Step("SMB-Paketdatei: FileStream öffnen für " + source,
                    () => NasPackageFile.OpenRead(source));
                try
                {
                    token.ThrowIfCancellationRequested();
                    return (Stream: stream, Length: preparation.Step("SMB-Paketdatei: Länge lesen", () => stream.Length));
                }
                catch { preparation.Step("SMB-Paketdatei: Handle nach Fehler schließen", stream.Dispose); throw; }
            }, result => result.Stream.Dispose(), token, openingWaitChanged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException(SmbAccessHelp.Explain(ex, source, write: false), ex); }
    }

    private static void ReportStatus(Action<string>? status, string message)
    {
        ClientLog.Write(LogSeverity.DEBUG, "Download", message);
        status?.Invoke(message);
    }
    private static string PackageUrl(string server, string gameId) =>
        server.TrimEnd('/') + "/api/games/" + Uri.EscapeDataString(gameId) + "/package";

    internal static async Task CopyAsync(Stream input, string destination, long? total,
        Action<long, long?> progress, CancellationToken token, string? nasSource = null, Action<string>? status = null)
    {
        ReportStatus(status, "Downloadziel: temporäre Datei öffnen …");
        await using var output = new FileStream(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int count;
        var firstRead = true;
        while (true)
        {
            var readClock = firstRead ? System.Diagnostics.Stopwatch.StartNew() : null;
            if (firstRead) ReportStatus(status, (nasSource is null ? "HTTP" : "NAS") + ": erster Lesezugriff beginnt (ReadAsync) …");
            try { count = await input.ReadAsync(buffer, token).ConfigureAwait(false); }
            catch (Exception ex) when (nasSource is not null && ex is IOException or UnauthorizedAccessException)
            {
                ReportStatus(status, $"NAS: {(firstRead ? "erster" : "weiterer")} Lesezugriff fehlgeschlagen; Windows-Code {NasNetworkOperations.ErrorCode(ex)?.ToString() ?? "unbekannt"}.");
                throw new IOException(SmbAccessHelp.Explain(ex, nasSource, write: false), ex);
            }
            if (firstRead)
            {
                ReportStatus(status, $"{(nasSource is null ? "HTTP" : "NAS")}: erster Lesezugriff beendet nach {readClock!.Elapsed.TotalMilliseconds:0} ms, {count} Bytes.");
                firstRead = false;
            }
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            received += count;
            progress(received, total);
        }
        await output.FlushAsync(token);
        if (total.HasValue && received != total.Value)
            throw new IOException("Die Paketübertragung ist unvollständig.");
    }
}
