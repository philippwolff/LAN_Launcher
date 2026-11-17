using System.IO;

namespace LanLauncher.Client.Logging;

internal static class ClientLog
{
    private static readonly AsyncLocal<string?> CurrentOperation = new();
    private static ClientLogStore? store;
    private static readonly object StoreGate = new();
    internal static string? OperationId => CurrentOperation.Value;
    internal static ClientLogStore Store
    {
        get
        {
            lock (StoreGate) return store ??= new ClientLogStore(new LogStoreOptions(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LAN_Launcher", "Logs")));
        }
    }

    internal static void Configure(ClientLogStore configured) { lock (StoreGate) store = configured; }
    internal static IDisposable BeginOperation(string component)
    {
        var previous = CurrentOperation.Value;
        var id = component + "-" + Guid.NewGuid().ToString("N")[..12];
        CurrentOperation.Value = id;
        Write(LogSeverity.INFO, component, "Vorgang gestartet.");
        return new Operation(() => { Write(LogSeverity.INFO, component, "Vorgang beendet."); CurrentOperation.Value = previous; });
    }
    internal static void Write(LogSeverity severity, string component, string message, Exception? error = null, string? operationId = null)
    {
        try { Store.Write(severity, component, message, error, operationId ?? OperationId); }
        catch (Exception) { /* A diagnostic failure must never change a transfer's behavior. */ }
    }

    private sealed class Operation(Action end) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; end(); } }
    }
}
