using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using LanLauncher.Client;

static class DownloadRegression
{
    private static IOException NetworkError(int code) => new("simulated SMB error", unchecked((int)0x80070000) | code);

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static async Task RunAsync(Action<bool, string> check, string directory)
    {
        check(NasNetworkOperations.IsTransient(NetworkError(59)) && !NasShareConnection.IsCredentialError(NetworkError(59)),
            "Unexpected network error 59 is transient, not a password failure");
        check(NasNetworkOperations.IsAuthenticationError(NetworkError(1326)) &&
            NasNetworkOperations.IsPermissionError(NetworkError(5)), "Typed Win32 HRESULTs distinguish login and permission errors");
        check(!SmbAccessHelp.Explain(NetworkError(59), @"\\nas\share\Games\game.zip", false).Contains("/delete"),
            "Temporary network errors do not recommend disconnecting SMB");
        var attempts = 0;
        var statuses = new ConcurrentQueue<string>();
        var answer = await NasNetworkOperations.ReadOnlyAsync(() =>
        { if (++attempts == 1) throw NetworkError(59); return 42; }, default, statuses.Enqueue);
        check(answer == 42 && attempts == 2 && statuses.Count == 1, "SMB read-only preparation retries once after a transient failure");
        attempts = 0;
        await ThrowsAsync<IOException>(() => NasNetworkOperations.ReadOnlyAsync<int>(() =>
        { attempts++; throw NetworkError(59); }, default));
        check(attempts == 2, "Persistent SMB failure stops after two attempts");
        foreach (var code in new[] { 5, 86, 1326, 1219, 2 })
        {
            attempts = 0;
            await ThrowsAsync<Win32Exception>(() => NasNetworkOperations.ReadOnlyAsync<int>(() =>
            { attempts++; throw new Win32Exception(code); }, default));
            check(attempts == 1, $"No automatic network retry for Windows error {code}");
        }
        attempts = 0;
        await ThrowsAsync<IOException>(() => NasNetworkOperations.BlockingAsync<int>(() =>
        { attempts++; throw NetworkError(59); }, default));
        check(attempts == 1, "Connection establishment never automatically retries");

        // Simulated synchronous SMB open that Windows completes after cancellation/timeout.
        foreach (var cancel in new[] { false, true })
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource();
            var operation = NasNetworkOperations.ReadOnlyAsync(() =>
            {
                started.TrySetResult(); release.Wait();
                return new TrackedDisposable(() => disposed.TrySetResult());
            }, cts.Token, discard: value => value.Dispose(), timeout: TimeSpan.FromMilliseconds(100));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (cancel)
                {
                    cts.Cancel();
                    await ThrowsAsync<OperationCanceledException>(() => operation);
                }
                else await ThrowsAsync<TimeoutException>(() => operation);
                check(!disposed.Task.IsCompleted, cancel ? "Cancel stops waiting for blocked SMB open" : "SMB preparation has a bounded timeout");
            }
            finally { release.Set(); }
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(disposed.Task.IsCompleted, "Late SMB handle is released after cancellation/timeout");
        }

        var httpAttempts = 0;
        using var timeoutHttp = new HttpClient(new DelegateHandler(async (_, token) =>
        { Interlocked.Increment(ref httpAttempts); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }));
        await ThrowsAsync<HttpRequestException>(() => NasNetworkOperations.MetadataAsync(timeoutHttp, "http://test/api/capabilities",
            default, timeout: TimeSpan.FromMilliseconds(30)));
        check(httpAttempts == 2, "Metadata timeout is finite even when HttpClient timeout is infinite");
        httpAttempts = 0;
        using (var cancelHttp = new CancellationTokenSource())
        {
            var request = NasNetworkOperations.MetadataAsync(timeoutHttp, "http://test/api/capabilities", cancelHttp.Token);
            cancelHttp.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => request);
            check(httpAttempts == 1, "User cancellation does not retry HTTP metadata");
        }
        httpAttempts = 0;
        using var unavailable = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(++httpAttempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))));
        using var recovered = await NasNetworkOperations.MetadataAsync(unavailable, "http://test/api/capabilities", default);
        check(recovered.IsSuccessStatusCode && httpAttempts == 2, "HTTP 503 metadata retries once");
        httpAttempts = 0;
        using var denied = new HttpClient(new DelegateHandler((_, _) =>
        { httpAttempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        await ThrowsAsync<HttpRequestException>(() => ServerTransferResolver.DetectAsync(denied, "http://test"));
        check(httpAttempts == 1, "HTTP authentication failure is not retried or mistaken for legacy HTTP");

        using (var release = new ManualResetEventSlim())
        using (var started = new ManualResetEventSlim())
        {
            await Task.Run(() => TestDispatcher.Run(async () =>
            {
                using var cancel = new CancellationTokenSource();
                var ui = SynchronizationContext.Current!;
                var waiting = NasNetworkOperations.ReadOnlyAsync(() =>
                { started.Set(); release.Wait(); return true; }, cancel.Token);
                try
                {
                    await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5)));
                    var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ui.Post(_ => heartbeat.SetResult(), null);
                    await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    using var health = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
                    using var response = await health.GetAsync("http://test/api/health");
                    check(response.IsSuccessStatusCode && !waiting.IsCompleted,
                        "Dispatcher heartbeat and independent server HTTP remain responsive while SMB preparation blocks");
                    cancel.Cancel();
                    await ThrowsAsync<OperationCanceledException>(() => waiting);
                    check(true, "Dispatcher can cancel blocked NAS preparation without waiting for Windows");
                }
                finally { release.Set(); }
            }));
        }

        var requests = new List<string>();
        using var metadata = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var json = requests.Last() == "/api/capabilities"
                ? System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, downloadMode = "smb", smbShare = @"\\nas\share" })
                : "{\"relativePath\":\"Games/game.zip\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        var phases = new List<string>();
        var package = await GamePackageTransfer.PrepareAsync(metadata, "http://test", "game", default, phases.Add);
        check(requests.SequenceEqual(new[] { "/api/capabilities", "/api/games/game/package-location" }) && phases.Count == 2,
            "Preparation fetches capabilities and location exactly once, reports both phases, touches no SMB path");
        var logins = 0;
        var noFile = Path.Combine(directory, "cancelled-prepared");
        await ThrowsAsync<OperationCanceledException>(() => GamePackageTransfer.DownloadPreparedAsync(metadata,
            package, noFile, (_, _) => { }, default, ensureNasConnected: _ =>
            { logins++; throw new OperationCanceledException(); }));
        check(requests.Count == 2 && logins == 1 && !File.Exists(noFile), "Prepared download does not repeat metadata and cancels before file access");

        var source = Path.Combine(directory, "source.zip");
        var bytes = new byte[512 * 1024];
        new Random(123).NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        var local = package with { Source = source };
        var destination = Path.Combine(directory, "download-copy.zip");
        // A dedicated SynchronizationContext simulates the WPF Dispatcher without any NAS.
        await Task.Run(() => TestDispatcher.Run(async () =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var copyOnUi = false;
            var loginOnUi = false;
            var transferred = 0L;
            await GamePackageTransfer.DownloadPreparedAsync(metadata, local, destination,
                (received, _) => { copyOnUi |= Environment.CurrentManagedThreadId == uiThread; transferred = received; },
                default, ensureNasConnected: _ => { loginOnUi = Environment.CurrentManagedThreadId == uiThread; return Task.CompletedTask; });
            check(!copyOnUi && loginOnUi && transferred == bytes.Length, "SMB copy runs off Dispatcher while login callback stays on Dispatcher");
        }));
        check((await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "Direct file transfer preserves complete package bytes");
        using var cancelCopy = new CancellationTokenSource();
        var partial = Path.Combine(directory, "cancelled-copy");
        await ThrowsAsync<OperationCanceledException>(() => GamePackageTransfer.DownloadPreparedAsync(metadata, local, partial,
            (_, _) => cancelCopy.Cancel(), cancelCopy.Token));
        check(new FileInfo(partial).Length < bytes.Length, "Cancellation stops copying without a restart");
        var destinationError = await ThrowsAsync<UnauthorizedAccessException>(() => GamePackageTransfer.DownloadPreparedAsync(metadata,
            local, directory, (_, _) => { }, default));
        check(!destinationError.Message.Contains("NAS-Lesezugriff"), "Local destination failure is not mislabeled as NAS read failure");
        using var interrupted = new FailingReadStream();
        var interruptedTarget = Path.Combine(directory, "interrupted");
        var readError = await ThrowsAsync<IOException>(() => GamePackageTransfer.CopyAsync(interrupted,
            interruptedTarget, 8, (_, _) => { }, default, @"\\nas\share\Games\game.zip"));
        check(interrupted.Reads == 2 && new FileInfo(interruptedTarget).Length == 4 && readError.Message.Contains("59"),
            "Mid-copy SMB error keeps native code and never restarts or appends a second copy");
        using var truncated = new MemoryStream(new byte[2]);
        await ThrowsAsync<IOException>(() => GamePackageTransfer.CopyAsync(truncated,
            Path.Combine(directory, "truncated"), 4, (_, _) => { }, default));
        check(true, "Truncated package is rejected");
    }
}

sealed class FailingReadStream : MemoryStream
{
    internal int Reads;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (++Reads > 1) throw new IOException("simulated broken read", unchecked((int)0x8007003b));
        buffer.Span[..4].Clear();
        return ValueTask.FromResult(4);
    }
}

sealed class TrackedDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token);
}

sealed class TestDispatcher : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    internal static void Run(Func<Task> action)
    {
        var previous = Current;
        var context = new TestDispatcher();
        SetSynchronizationContext(context);
        try
        {
            var work = action();
            _ = work.ContinueWith(_ => context.queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context.queue.GetConsumingEnumerable()) callback(state);
            work.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); context.queue.Dispose(); }
    }
}
