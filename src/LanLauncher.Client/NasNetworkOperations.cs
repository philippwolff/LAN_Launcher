using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using LanLauncher.Client.Logging;

namespace LanLauncher.Client;

internal static class NasNetworkOperations
{
    internal static int? ErrorCode(Exception error) => error is Win32Exception win ? win.NativeErrorCode :
        (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? error.HResult & 0xffff : null;

    internal static bool IsAuthenticationError(Exception error) => ErrorCode(error) is 86 or 1326 or 1312;
    internal static bool IsPermissionError(Exception error) => error is UnauthorizedAccessException || ErrorCode(error) == 5;
    internal static bool IsTransient(Exception error) =>
        (error is IOException or Win32Exception) && ErrorCode(error) is 59 or 64 or 121 or 1231 or 1232;

    internal static Task<T> ReadOnlyAsync<T>(Func<T> operation, CancellationToken token,
        Action<string>? status = null, Action<T>? discard = null, TimeSpan? timeout = null) =>
        BlockingAsync(operation, token, status, discard, timeout, retryReadOnly: true);

    // Never detach a destination writer. Connection establishment uses no automatic retry.
    internal static async Task<T> BlockingAsync<T>(Func<T> operation, CancellationToken token,
        Action<string>? status = null, Action<T>? discard = null, TimeSpan? timeout = null, bool retryReadOnly = false,
        Action<Task>? trackWork = null, Func<string>? pendingDescription = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var work = Task.Run(() => { token.ThrowIfCancellationRequested(); return operation(); });
            trackWork?.Invoke(work);
            try
            {
                return await work.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                ClientLog.Write(LogSeverity.WARN, "SMB", "Warten beendet; Windows-Worker kann weiterlaufen. " + pendingDescription?.Invoke(), ex);
                // Windows synchronous SMB opens cannot reliably be interrupted. The UI stops
                // waiting; release an eventual handle and observe an eventual exception.
                var cleanup = work.ContinueWith(done =>
                {
                    ClientLog.Write(LogSeverity.INFO, "SMB", "Zuvor abgebrochener/abgelaufener Worker ist jetzt beendet. " + pendingDescription?.Invoke());
                    if (done.Status == TaskStatus.RanToCompletion) discard?.Invoke(done.Result);
                    else _ = done.Exception;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                trackWork?.Invoke(cleanup);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw new TimeoutException($"NAS-Vorbereitung nach {(timeout ?? TimeSpan.FromSeconds(15)).TotalSeconds:0.##} Sekunden ohne Antwort beendet. " +
                    "Windows kann den SMB-Aufruf noch abschließen. " + pendingDescription?.Invoke(), ex);
            }
            catch (Exception ex) when (retryReadOnly && attempt < 2 && IsTransient(ex))
            {
                ClientLog.Write(LogSeverity.WARN, "SMB", "Temporärer Fehler; zweiter und letzter lesender Versuch.", ex);
                status?.Invoke($"Temporärer NAS-Netzwerkfehler (Windows {ErrorCode(ex)}). Zweiter und letzter Versuch …");
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }
    }

    // GET metadata only: bounded independently of an infinite package-download timeout.
    internal static async Task<HttpResponseMessage> MetadataAsync(HttpClient client, string url,
        CancellationToken token, Action<string>? status = null, TimeSpan? timeout = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
                if (attempt < 2 && response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    response.Dispose();
                    status?.Invoke("HTTP-Metadaten vorübergehend nicht verfügbar. Zweiter und letzter Versuch …");
                    await Task.Delay(400, token).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException { StatusCode: null } ||
                ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                if (attempt == 2)
                    throw new HttpRequestException($"HTTP-Metadaten {new Uri(url).AbsolutePath} vom Launcher-Server nicht erreichbar " +
                        $"(maximal zwei Versuche, je {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds:0.##} Sekunden).", ex);
                status?.Invoke("HTTP-Metadaten: Netzwerkfehler oder Zeitlimit. Zweiter und letzter Versuch …");
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }
    }
}
