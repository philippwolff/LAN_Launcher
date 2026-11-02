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
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public long BytesPerSecond { get; }

    public DownloadBandwidthLimiter(long bytesPerSecond)
    {
        if (bytesPerSecond < 65536) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        BytesPerSecond = bytesPerSecond;
    }

    public long Join()
    {
        lock (gate)
        {
            var id = ++nextId;
            nextSendSeconds[id] = clock.Elapsed.TotalSeconds;
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
            foreach (var key in nextSendSeconds.Keys.ToArray())
                nextSendSeconds[key] = Math.Min(nextSendSeconds[key], clock.Elapsed.TotalSeconds);
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
            waitSeconds = Math.Max(0, next - now);
            nextSendSeconds[id] = Math.Max(now, next) + duration;
        }
        if (waitSeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
    }
}
