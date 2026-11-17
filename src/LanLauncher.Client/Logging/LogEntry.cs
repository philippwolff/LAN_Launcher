using System.ComponentModel;
using System.Text;

namespace LanLauncher.Client.Logging;

internal enum LogSeverity { DEBUG, INFO, WARN, ERROR }

internal sealed record LogEntry(Guid Id, DateTimeOffset Timestamp, LogSeverity Severity,
    string Component, string Message, string? OperationId, string? ExceptionDetails)
{
    public string LocalTime => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    public string DisplayMessage => Message + (ExceptionDetails is null ? "" : "\n" + ExceptionDetails);
    public string DisplayComponent => Component + (OperationId is null ? "" : "\n" + OperationId);
}

internal sealed record LogSnapshot(long Version, IReadOnlyList<LogEntry> Entries, string StorageStatus);

// A later server-log adapter can implement this interface without changing the viewer.
internal interface ILogSource
{
    string Name { get; }
    Task<LogSnapshot> ReadAsync(CancellationToken token = default);
}

internal static class LogFormat
{
    internal static string Exception(Exception? error)
    {
        if (error is null) return "";
        var builder = new StringBuilder();
        var pending = new Queue<(Exception Error, int Depth)>();
        pending.Enqueue((error, 0));
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0 && seen.Count < 16)
        {
            var (item, depth) = pending.Dequeue();
            if (!seen.Add(item)) continue;
            var code = item is Win32Exception win ? win.NativeErrorCode :
                (item.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? item.HResult & 0xffff : (int?)null;
            builder.AppendLine($"{new string(' ', depth * 2)}{item.GetType().FullName}; HRESULT=0x{item.HResult:X8}; Windows={code?.ToString() ?? "–"}: {item.Message}");
            if (item.StackTrace is { } stack) builder.AppendLine(stack);
            if (depth < 7)
            {
                if (item is AggregateException aggregate)
                    foreach (var inner in aggregate.InnerExceptions.Take(16)) pending.Enqueue((inner, depth + 1));
                else if (item.InnerException is { } inner) pending.Enqueue((inner, depth + 1));
            }
        }
        return LogPrivacy.Redact(builder.ToString());
    }

    internal static string Export(IEnumerable<LogEntry> entries, bool anonymize)
    {
        var builder = new StringBuilder("Zeit\tStufe\tKomponente\tVorgang\tMeldung\n");
        foreach (var entry in entries)
            builder.AppendLine($"{entry.LocalTime}\t{entry.Severity}\t{entry.Component}\t{entry.OperationId}\t{entry.DisplayMessage}");
        return anonymize ? LogPrivacy.Anonymize(builder.ToString()) : LogPrivacy.Redact(builder.ToString());
    }
}
