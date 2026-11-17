using System.Collections.Concurrent;
using LanLauncher.Client;

static class PreparationRegression
{
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    internal static async Task RunAsync(Action<bool, string> check, string directory)
    {
        var host = "nas-preparation-tests-" + Guid.NewGuid().ToString("N");
        var share = @"\\" + host + @"\Daten";
        var messages = new ConcurrentQueue<string>();
        using (var preparation = await NasPreparation.AcquireAsync(share, false, "Reader test", default, messages.Enqueue))
        {
            var hasAccess = await preparation.RunAsync("Zugriff prüfen", () =>
                NasShareConnection.HasRequiredAccess(directory, false, preparation), default, readOnly: true);
            check(hasAccess && messages.Any(m => m.Contains("Enumerator öffnen")) &&
                messages.Any(m => m.Contains("MoveNext")) && messages.Any(m => m.Contains("Enumerator schließen")),
                "Real access probe diagnoses enumerator creation, MoveNext and disposal separately");
            messages.Clear();
            await preparation.RunAsync("Sitzung wiederverwenden", () =>
            {
                NasShareConnection.EnsureConnected(directory, false, "password-that-must-not-be-used", preparation: preparation);
                return true;
            }, default);
            check(messages.Any(m => m.Contains("wird wiederverwendet")) &&
                !messages.Any(m => m.Contains("SMB-Verbindungsaufbau:") || m.Contains("CredRead") || m.Contains("Admin-Schreibprüfung")),
                "Working reader access performs no reconnect, credential read or admin write probe");
            check(messages.All(m => !m.Contains("password-that-must-not-be-used")), "Preparation diagnostics never include supplied passwords");
        }
        messages.Clear();
        using (var preparation = await NasPreparation.AcquireAsync(share, true, "Admin test", default, messages.Enqueue))
        {
            check(await preparation.RunAsync("Admin-Zugriff", () =>
                NasShareConnection.HasRequiredAccess(directory, true, preparation), default), "Admin permission probe remains enabled");
            check(messages.Any(m => m.Contains("launcher_admin")) && messages.Any(m => m.Contains("Testdatei anlegen")) &&
                !Directory.EnumerateFiles(Path.Combine(directory, "Games"), ".lanlauncher-write-test-*").Any(),
                "Admin diagnostics identify write probe and cleanup with distinct role");
        }

        // A successful operation does not release the preparation while the login workflow/dialog still owns it.
        var first = await NasPreparation.AcquireAsync(share, false, "First", default);
        var second = NasPreparation.AcquireAsync(@"\\" + host.ToUpperInvariant() + @".\OtherShare", true, "Second", default);
        check(!second.IsCompleted, "Same-host preparations serialize across case, shares and reader/admin roles");
        using (var independent = await NasPreparation.AcquireAsync(@"\\independent-" + host + @"\Daten", false, "Other host", default,
            waitTimeout: TimeSpan.FromMilliseconds(100)))
            check(true, "Different NAS host is not blocked by the preparation gate");
        first.Dispose();
        first.Dispose();
        using (var next = await second.WaitAsync(TimeSpan.FromSeconds(5)))
            check(true, "Next preparation starts after prior workflow completes; duplicate dispose is harmless");

        using (var failing = await NasPreparation.AcquireAsync(share, false, "Failed worker", default))
            await ThrowsAsync<InvalidOperationException>(() => failing.RunAsync<bool>("Worker fails", () =>
            { throw new InvalidOperationException("simulated worker failure"); }, default));
        using (var afterFailure = await NasPreparation.AcquireAsync(share, false, "After failure", default,
            waitTimeout: TimeSpan.FromMilliseconds(100)))
            check(true, "Worker failure does not leak host gate");
        await ThrowsAsync<InvalidOperationException>(() => NasPreparation.AcquireAsync(share, false, "Broken status sink", default,
            _ => throw new InvalidOperationException("simulated status failure")));
        using (var afterStatusFailure = await NasPreparation.AcquireAsync(share, false, "After status failure", default,
            waitTimeout: TimeSpan.FromMilliseconds(100)))
            check(true, "Failed initial diagnostic callback does not leak host gate");

        foreach (var cancel in new[] { false, true })
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            messages.Clear();
            var preparation = await NasPreparation.AcquireAsync(share, false, "Blocked reader", default, messages.Enqueue);
            var operation = preparation.RunAsync("Bestehenden Zugriff prüfen", () =>
                preparation.Step("Verzeichnisauflistung: MoveNext simuliert blockiert", () =>
                {
                    started.TrySetResult();
                    release.Wait();
                    return new TrackedDisposable(() => finished.TrySetResult());
                }), cancellation.Token, readOnly: true, discard: handle => handle.Dispose(), timeout: TimeSpan.FromMilliseconds(250));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    await ThrowsAsync<OperationCanceledException>(() => operation);
                }
                else
                {
                    var error = await ThrowsAsync<TimeoutException>(() => operation);
                    check(error.Message.Contains("MoveNext simuliert blockiert") && error.Message.Contains("launcher_reader"),
                        "Timeout identifies exact pending worker operation and NAS role");
                }
                preparation.Dispose(); // The user has closed the timed-out/cancelled workflow.
                var blocked = await ThrowsAsync<TimeoutException>(() => NasPreparation.AcquireAsync(
                    @"\\" + host + @"\DifferentShare", true, "Reconnect attempt", default,
                    waitTimeout: TimeSpan.FromMilliseconds(50)));
                check(blocked.Message.Contains("kein weiterer SMB-Aufruf gestartet") && blocked.Message.Contains("MoveNext"),
                    cancel ? "Cancelled native worker retains host gate; no parallel admin reconnect" :
                        "Timed-out native worker retains host gate; no parallel admin reconnect");
                check(blocked.Message.Contains("Aufrufer beendet=True") && blocked.Message.Contains("Startkontext:") &&
                    blocked.Message.Contains("Worker-Kontext:") && blocked.Message.Contains("LogonId="),
                    "Waiting-client error identifies abandoned active worker and both Windows contexts");
                using var cancelWaiter = new CancellationTokenSource();
                var waiter = NasPreparation.AcquireAsync(share, false, "Waiting client", cancelWaiter.Token);
                cancelWaiter.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => waiter);
                check(true, "Waiting for host gate is cancellable without starting a worker");
            }
            finally { preparation.Dispose(); release.Set(); }
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var resumed = await NasPreparation.AcquireAsync(share, true, "After native completion", default);
            check(true, "Host gate is released only after late native completion and handle cleanup");
        }

        // No synchronous Dispatcher.Invoke while a worker owns the host gate.
        await Task.Run(() => TestDispatcher.Run(async () =>
        {
            var ui = SynchronizationContext.Current!;
            var uiThread = Environment.CurrentManagedThreadId;
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var preparation = await NasPreparation.AcquireAsync(share, false, "Dispatcher test", default,
                message => ui.Post(_ => { if (message.Contains("Worker-Diagnose")) delivered.TrySetResult(); }, null));
            var workerThread = await preparation.RunAsync("Worker-Diagnose", () => Environment.CurrentManagedThreadId, default);
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(workerThread != uiThread, "Worker diagnosis posts to Dispatcher without a synchronous dispatcher/host-lock cycle");
        }));
    }
}
