using System.Diagnostics;

namespace LanLauncher.Server;

/// <summary>
/// Shares a configurable aggregate download budget equally between active package transfers.
/// Recalculates each transfer's share on every chunk, so new clients receive bandwidth promptly.
/// </summary>
public sealed class DownloadBandwidthLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<long, double> nextSendSeconds = new();
    private long nextId;
    private readonly Dictionary<long, string> owners = new();
    private readonly Dictionary<long, Queue<(double Time, int Bytes)>> recentBytes = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public long BytesPerSecond { get; }

    public DownloadBandwidthLimiter(long bytesPerSecond)
    {
        if (bytesPerSecond < 65536) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        BytesPerSecond = bytesPerSecond;
    }

    public long Join(string clientId)
    {
        lock (gate)
        {
            var id = ++nextId;
            nextSendSeconds[id] = clock.Elapsed.TotalSeconds;
            owners[id] = clientId;
            recentBytes[id] = new Queue<(double Time, int Bytes)>();
            // Rebalance existing transfers immediately when the participant count changes.
            foreach (var key in nextSendSeconds.Keys.ToArray())
                nextSendSeconds[key] = Math.Min(nextSendSeconds[key], clock.Elapsed.TotalSeconds);
            return id;
        }
    }

    public void Leave(long id)
    {
        lock (gate)
        {
            nextSendSeconds.Remove(id);
            owners.Remove(id);
            recentBytes.Remove(id);
            foreach (var key in nextSendSeconds.Keys.ToArray())
                nextSendSeconds[key] = Math.Min(nextSendSeconds[key], clock.Elapsed.TotalSeconds);
        }
    }

    public IReadOnlyDictionary<string, double> RatesByClient()
    {
        lock (gate)
        {
            var now = clock.Elapsed.TotalSeconds;
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (id, owner) in owners)
            {
                var samples = recentBytes[id];
                while (samples.Count > 0 && samples.Peek().Time < now - 5) samples.Dequeue();
                var bytes = samples.Sum(x => (long)x.Bytes);
                result[owner] = result.GetValueOrDefault(owner) + bytes / 5.0;
            }
            return result;
        }
    }

    public void RecordSent(long id, int bytes)
    {
        lock (gate)
        {
            if (!recentBytes.TryGetValue(id, out var samples)) return;
            var now = clock.Elapsed.TotalSeconds;
            samples.Enqueue((now, bytes));
            while (samples.Count > 0 && samples.Peek().Time < now - 5) samples.Dequeue();
        }
    }

    public async Task WaitAsync(long id, int bytes, CancellationToken cancellationToken)
    {
        double waitSeconds;
        lock (gate)
        {
            if (!nextSendSeconds.TryGetValue(id, out var next)) return;
            var now = clock.Elapsed.TotalSeconds;
            var duration = (double)bytes * nextSendSeconds.Count / BytesPerSecond;
            var scheduled = Math.Max(now, next) + duration;
            waitSeconds = Math.Max(0, scheduled - now);
            nextSendSeconds[id] = scheduled;
        }
        if (waitSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
    }
}
