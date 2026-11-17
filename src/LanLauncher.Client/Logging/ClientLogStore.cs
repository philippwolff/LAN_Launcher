using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace LanLauncher.Client.Logging;

internal sealed record LogStoreOptions(string Directory, long MaxTotalBytes = 20 * 1024 * 1024,
    long MaxFileBytes = 1024 * 1024, int RetentionDays = 7, int MemoryEntries = 2000, int QueueCapacity = 4096);

internal sealed class ClientLogStore : ILogSource, IAsyncDisposable
{
    private sealed record Command(LogEntry? Entry, TaskCompletionSource? Flush = null);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly LogStoreOptions options;
    private readonly Channel<Command> queue;
    private readonly Queue<LogEntry> recent = new();
    private readonly object memoryGate = new();
    private readonly Task writer;
    private long version, dropped, maxTotalBytes;
    private string storageStatus = "Protokoll wird geladen …";
    private FileStream? file;
    private string? currentPath;
    private int disposed;

    internal ClientLogStore(LogStoreOptions options)
    {
        if (options.MaxFileBytes < 128 || options.MaxTotalBytes < options.MaxFileBytes * 2 || options.RetentionDays < 7 || options.MemoryEntries < 1 || options.QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        maxTotalBytes = options.MaxTotalBytes;
        queue = Channel.CreateBounded<Command>(new BoundedChannelOptions(options.QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        writer = Task.Run(ConsumeAsync);
    }

    public string Name => "Client";
    internal string DirectoryPath => options.Directory;
    internal void SetBudget(long bytes)
    {
        if (bytes < 2 * options.MaxFileBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        Interlocked.Exchange(ref maxTotalBytes, bytes);
        Write(LogSeverity.INFO, "Protokoll", $"Speichergrenze auf {bytes / 1048576.0:0.##} MiB gesetzt.");
    }

    internal void Write(LogSeverity severity, string component, string message, Exception? error = null, string? operationId = null)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + " [gekürzt]";
        var details = LogFormat.Exception(error);
        var entry = new LogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, severity,
            Limit(LogPrivacy.Redact(component), 100), Limit(LogPrivacy.Redact(message), 4096),
            operationId is null ? null : Limit(LogPrivacy.Redact(operationId), 100),
            details.Length == 0 ? null : Limit(details, 8192));
        if (!queue.Writer.TryWrite(new Command(entry))) Interlocked.Increment(ref dropped);
    }

    public Task<LogSnapshot> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (memoryGate) return Task.FromResult(new LogSnapshot(version, recent.ToArray(), Volatile.Read(ref storageStatus)));
    }

    internal async Task FlushAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref disposed) != 0) { await writer.WaitAsync(token); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Command(null, completion), token);
        await completion.Task.WaitAsync(token);
    }

    private void Remember(LogEntry entry)
    {
        lock (memoryGate)
        {
            recent.Enqueue(entry);
            while (recent.Count > options.MemoryEntries) recent.Dequeue();
            version++;
        }
    }

    private async Task LoadHistoryAsync()
    {
        System.IO.Directory.CreateDirectory(options.Directory);
        Maintain(0);
        foreach (var path in System.IO.Directory.EnumerateFiles(options.Directory, "client-*.jsonl").Order(StringComparer.Ordinal))
        {
            try
            {
                using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                while (await input.ReadLineAsync() is { } line)
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<LogEntry>(line, Json) is { } entry)
                            Remember(entry with { Message = LogPrivacy.Redact(entry.Message), ExceptionDetails = entry.ExceptionDetails is null ? null : LogPrivacy.Redact(entry.ExceptionDetails) });
                    }
                    catch (JsonException) { /* An interrupted final line is not a valid entry. */ }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Maintain(long reserve)
    {
        var files = new DirectoryInfo(options.Directory).GetFiles("client-*.jsonl").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var cutoff = DateTime.UtcNow.Date.AddDays(-options.RetentionDays);
        long total = files.Sum(f => f.Length);
        foreach (var info in files)
        {
            if (info.FullName.Equals(currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (info.LastWriteTimeUtc >= cutoff && total + reserve <= Interlocked.Read(ref maxTotalBytes)) continue;
            try { var length = info.Length; info.Delete(); total -= length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (total + reserve > Interlocked.Read(ref maxTotalBytes))
            throw new IOException("Speichergrenze erreicht; belegte Protokolldateien können nicht entfernt werden.");
    }

    private async Task PersistAsync(LogEntry entry)
    {
        Remember(entry); // Logging failure must not hide diagnostics from the current UI.
        try
        {
            System.IO.Directory.CreateDirectory(options.Directory);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
            if (bytes.Length > options.MaxFileBytes)
            {
                entry = entry with { Message = "Eintrag überschreitet Dateigrenze; Details gekürzt.", ExceptionDetails = null };
                bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
            }
            if (bytes.Length > options.MaxFileBytes)
            {
                Volatile.Write(ref storageStatus, "Dateigrenze zu klein für diesen Eintrag; Eintrag bleibt im Speicher.");
                return;
            }
            if (file is not null && (file.Length + bytes.Length > options.MaxFileBytes || File.GetLastWriteTimeUtc(currentPath!).Date != DateTime.UtcNow.Date))
            { await file.DisposeAsync(); file = null; currentPath = null; }
            Maintain(bytes.Length);
            if (file is null)
            {
                currentPath = Path.Combine(options.Directory, $"client-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
                file = new FileStream(currentPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            }
            await file.WriteAsync(bytes);
            await file.FlushAsync();
            Volatile.Write(ref storageStatus, $"Lokal gespeichert · maximal {Interlocked.Read(ref maxTotalBytes) / 1048576.0:0.##} MiB · Aufbewahrung mindestens 7 Tage, sofern Speicher verfügbar");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref storageStatus, "Dateiprotokoll nicht verfügbar: " + error.GetType().Name + $" (0x{error.HResult:X8}). Aktuelle Einträge bleiben im Speicher.");
            if (file is not null) { try { await file.DisposeAsync(); } catch (IOException) { } file = null; currentPath = null; }
        }
    }

    private async Task ConsumeAsync()
    {
        try { await LoadHistoryAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Volatile.Write(ref storageStatus, "Vorherige Protokolle nicht lesbar: " + error.GetType().Name); }
        await foreach (var command in queue.Reader.ReadAllAsync())
        {
            var lost = Interlocked.Exchange(ref dropped, 0);
            if (lost > 0) await PersistAsync(new LogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, LogSeverity.WARN,
                "Protokoll", $"Warteschlange voll: {lost} Einträge verworfen, damit der Client nicht blockiert.", null, null));
            if (command.Entry is { } entry) await PersistAsync(entry);
            if (command.Flush is { } completion) completion.TrySetResult();
        }
        if (file is not null) await file.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) queue.Writer.TryComplete();
        await writer.ConfigureAwait(false);
    }
}
