using System.Collections.Concurrent;
using System.Diagnostics;
using LanLauncher.Client.Logging;

namespace LanLauncher.Client;

/// <summary>Serializes preparation across shares and roles on the same SMB host.</summary>
internal sealed class NasPreparation : IDisposable
{
    private sealed class HostState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal NasPreparation? Current;
    }

    private static readonly ConcurrentDictionary<string, HostState> Hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HostState host;
    private readonly Action<string>? status;
    private readonly string identity;
    private readonly string? operationId = ClientLog.OperationId;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string callerContext = NasWindowsContext.Capture();
    private string workerContext = "noch nicht gestartet";
    private string phase = "Vorbereitung auf dem UI-Thread";
    private int owners = 1;
    private int disposed;

    private NasPreparation(HostState host, string share, bool admin, string origin, Action<string>? status)
    {
        this.host = host;
        this.status = status;
        identity = $"NAS[{Guid.NewGuid().ToString("N")[..8]}] {origin}, {(admin ? "launcher_admin" : "launcher_reader")}, {share}";
    }

    internal string PendingDescription => $"{identity}: {Volatile.Read(ref phase)} (Auftrag seit {clock.Elapsed.TotalSeconds:0.0} s)";
    internal string DiagnosticDescription => PendingDescription +
        $"\nAufrufer beendet={Volatile.Read(ref disposed) != 0}; ausstehende Worker/Cleanup-Aufgaben=" +
        Math.Max(0, Volatile.Read(ref owners) - (Volatile.Read(ref disposed) == 0 ? 1 : 0)) +
        "\nStartkontext: " + callerContext + "\nWorker-Kontext: " + Volatile.Read(ref workerContext);

    private static string HostKey(string share)
    {
        if (!share.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("NAS-Vorbereitung benötigt einen UNC-Pfad.", nameof(share));
        var parts = share[2..].Split('\\');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("NAS-Vorbereitung benötigt Host und Freigabe.", nameof(share));
        return parts[0].TrimEnd('.');
    }

    internal static async Task<NasPreparation> AcquireAsync(string share, bool admin, string origin,
        CancellationToken token, Action<string>? status = null, TimeSpan? waitTimeout = null)
    {
        token.ThrowIfCancellationRequested();
        var state = Hosts.GetOrAdd(HostKey(share), _ => new HostState());
        if (!state.Gate.Wait(0))
        {
            ClientLog.Write(LogSeverity.WARN, "SMB", "Vorbereitung belegt; kein paralleler SMB-Aufruf. " +
                (Volatile.Read(ref state.Current)?.DiagnosticDescription ?? "Vorheriger Auftrag wird abgeschlossen."));
            status?.Invoke("NAS: auf laufende Vorbereitung warten. " +
                (Volatile.Read(ref state.Current)?.PendingDescription ?? "Vorheriger Auftrag wird abgeschlossen."));
            // Never hold a monitor or synchronously wait for the Dispatcher.
            if (!await state.Gate.WaitAsync(waitTimeout ?? TimeSpan.FromSeconds(15), token))
                throw new TimeoutException("NAS-Vorbereitung noch belegt; kein weiterer SMB-Aufruf gestartet. " +
                    (Volatile.Read(ref state.Current)?.DiagnosticDescription ?? "Vorheriger Auftrag wird abgeschlossen."));
        }
        if (token.IsCancellationRequested)
        {
            state.Gate.Release();
            token.ThrowIfCancellationRequested();
        }
        var preparation = new NasPreparation(state, share, admin, origin, status);
        Volatile.Write(ref state.Current, preparation);
        try
        {
            Trace.WriteLine(preparation.identity + ": Startkontext: " + preparation.callerContext);
            ClientLog.Write(LogSeverity.INFO, "NAS", preparation.identity + ": Startkontext: " + preparation.callerContext);
            preparation.Report("Host-Sperre übernommen; vorhandene Sitzung wird zuerst über tatsächlichen Zugriff geprüft");
            return preparation;
        }
        catch { preparation.Dispose(); throw; }
    }

    internal void Report(string operation)
    {
        Volatile.Write(ref phase, operation);
        var message = PendingDescription;
        Trace.WriteLine(message);
        ClientLog.Write(LogSeverity.DEBUG, "SMB", message, operationId: operationId);
        status?.Invoke(message);
    }

    internal T Step<T>(string operation, Func<T> action)
    {
        Report(operation + " – gestartet auf Worker " + Environment.CurrentManagedThreadId);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = action();
            Report($"{operation} – beendet nach {elapsed.Elapsed.TotalMilliseconds:0} ms");
            return result;
        }
        catch (Exception error)
        {
            ClientLog.Write(error is OperationCanceledException ? LogSeverity.WARN : LogSeverity.ERROR,
                "SMB", operation + " fehlgeschlagen oder abgebrochen.", error, operationId);
            Report($"{operation} – Fehler nach {elapsed.Elapsed.TotalMilliseconds:0} ms: " +
                $"{error.GetType().Name}, Windows {NasNetworkOperations.ErrorCode(error)?.ToString() ?? "–"}");
            throw;
        }
    }

    internal void Step(string operation, Action action) => Step(operation, () => { action(); return true; });

    internal Task<T> RunAsync<T>(string operation, Func<T> action, CancellationToken token,
        bool readOnly = false, Action<T>? discard = null, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Volatile.Write(ref workerContext, "noch nicht gestartet");
        Report(operation + " – wartet auf Worker-Start");
        return NasNetworkOperations.BlockingAsync(() => ExecuteOnWorker(operation, action), token, status, discard,
            timeout, retryReadOnly: readOnly, trackWork: Track, pendingDescription: () => DiagnosticDescription);
    }

    internal Task<T> RunOpeningAsync<T>(Func<T> action, Action<T> discard, CancellationToken token,
        Action<bool>? waitingChanged = null, TimeSpan? noticeAfter = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Volatile.Write(ref workerContext, "noch nicht gestartet");
        Report("Paketdatei öffnen – wartet auf Worker-Start");
        return SmbFileOpening.WaitAsync(() => ExecuteOnWorker("Paketdatei öffnen und Länge lesen", action),
            discard, token, waitingChanged, Track, () => DiagnosticDescription, noticeAfter);
    }

    private T ExecuteOnWorker<T>(string operation, Func<T> action)
    {
        Volatile.Write(ref workerContext, NasWindowsContext.Capture());
        Trace.WriteLine(identity + ": Worker-Kontext: " + Volatile.Read(ref workerContext));
        ClientLog.Write(LogSeverity.DEBUG, "SMB", identity + ": Worker-Kontext: " + Volatile.Read(ref workerContext), operationId: operationId);
        return Step(operation, action);
    }

    private void Track(Task work)
    {
        Interlocked.Increment(ref owners);
        // The gate belongs to the underlying work, not merely the timed/cancelled await.
        _ = work.ContinueWith(_ => ReleaseOwner(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ReleaseOwner()
    {
        if (Interlocked.Decrement(ref owners) != 0) return;
        Trace.WriteLine(identity + ": alle Worker beendet; Host-Sperre freigegeben");
        ClientLog.Write(LogSeverity.INFO, "SMB", identity + ": alle Worker/Cleanup beendet; Host-Sperre freigegeben.", operationId: operationId);
        Volatile.Write(ref host.Current, null);
        host.Gate.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            ReleaseOwner();
            if (Volatile.Read(ref owners) > 0)
                ClientLog.Write(LogSeverity.WARN, "SMB", "Aufrufer beendet; Windows-Worker/Cleanup läuft weiterhin, Host bleibt gesperrt. " + DiagnosticDescription,
                    operationId: operationId);
        }
    }
}
