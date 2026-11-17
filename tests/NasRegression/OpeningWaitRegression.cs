using LanLauncher.Client;

static class OpeningWaitRegression
{
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    internal static async Task RunAsync(Action<bool, string> check, string directory)
    {
        var host = @"\\opening-wait-" + Guid.NewGuid().ToString("N") + @"\Games";
        using (var release = new ManualResetEventSlim())
        using (var preparation = await NasPreparation.AcquireAsync(host, false, "Slow open test", default))
        {
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hints = new System.Collections.Concurrent.ConcurrentQueue<bool>();
            var calls = 0;
            var opening = preparation.RunOpeningAsync(() =>
            {
                Interlocked.Increment(ref calls);
                release.Wait();
                return new MemoryStream(new byte[] { 1, 2, 3, 4 });
            }, stream => stream.Dispose(), default, waiting =>
            {
                hints.Enqueue(waiting);
                if (waiting) notified.TrySetResult();
            }, TimeSpan.FromMilliseconds(30));
            try
            {
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(!opening.IsCompleted && calls == 1, "15-second-style notice does not cancel or restart a slow SMB opening");
                await Task.Delay(60);
                check(!opening.IsCompleted && calls == 1, "Continuing to wait beyond the notice keeps the same native task alive");
                await ThrowsAsync<TimeoutException>(() => NasPreparation.AcquireAsync(host, true, "Concurrent open", default,
                    waitTimeout: TimeSpan.FromMilliseconds(25)));
                check(calls == 1, "Concurrent preparation cannot start another opening while the first is pending");
            }
            finally { release.Set(); }
            using var input = await opening.WaitAsync(TimeSpan.FromSeconds(5));
            var target = Path.Combine(directory, "after-slow-open");
            await GamePackageTransfer.CopyAsync(input, target, 4, (_, _) => { }, default);
            check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && hints.SequenceEqual(new[] { true, false }),
                "Successful delayed open transitions into copying and clears its waiting notice automatically");
        }

        foreach (var afterNotice in new[] { false, true })
        {
            using var release = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var preparation = await NasPreparation.AcquireAsync(host, false, "Cancel open test", default);
            var opening = preparation.RunOpeningAsync(() =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(); release.Wait();
                return new TrackedDisposable(() => closed.TrySetResult());
            }, handle => handle.Dispose(), cancellation.Token, waiting => { if (waiting) shown.TrySetResult(); },
                afterNotice ? TimeSpan.FromMilliseconds(25) : TimeSpan.FromSeconds(5));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (afterNotice) await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => opening);
                preparation.Dispose();
                check(!closed.Task.IsCompleted && calls == 1, afterNotice
                    ? "Cancel after notice ends the download await while its native open is still pending"
                    : "Cancel before notice ends the download await while its native open is still pending");
                await ThrowsAsync<TimeoutException>(() => NasPreparation.AcquireAsync(host, false, "Do not overlap", default,
                    waitTimeout: TimeSpan.FromMilliseconds(25)));
                check(true, "Cancelled opening retains the host gate until native completion and cleanup");
            }
            finally { preparation.Dispose(); release.Set(); }
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var next = await NasPreparation.AcquireAsync(host, false, "After cleanup", default);
            check(calls == 1, "Late handle is disposed and host gate is reusable without repeating the cancelled open");
        }

        using (var preparation = await NasPreparation.AcquireAsync(host, false, "Failed open", default))
        {
            var calls = 0;
            await ThrowsAsync<IOException>(() => preparation.RunOpeningAsync<IDisposable>(() =>
            { calls++; throw new IOException("native open failed", unchecked((int)0x8007003b)); }, handle => handle.Dispose(), default));
            check(calls == 1, "Native open failure propagates without retrying the open behind the waiting UI");
        }
        using (var preparation = await NasPreparation.AcquireAsync(host, false, "Fast open", default))
        {
            var hintCount = 0;
            using var handle = await preparation.RunOpeningAsync(() => new MemoryStream(), value => value.Dispose(), default,
                _ => hintCount++, TimeSpan.FromSeconds(5));
            check(hintCount == 0, "Fast opens do not show a spurious slow-opening notice");
        }
        using (var preparation = await NasPreparation.AcquireAsync(host, false, "Already cancelled", default))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var calls = 0;
            await ThrowsAsync<OperationCanceledException>(() => preparation.RunOpeningAsync(() =>
            { calls++; return new MemoryStream(); }, value => value.Dispose(), cancellation.Token));
            check(calls == 0, "Already cancelled download never starts an SMB open");
        }

        using (var releaseOpen = new ManualResetEventSlim())
        using (var releaseCleanup = new ManualResetEventSlim())
        using (var cancellation = new CancellationTokenSource())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preparation = await NasPreparation.AcquireAsync(host, false, "Slow cleanup", default);
            var opening = preparation.RunOpeningAsync(() =>
            {
                started.TrySetResult(); releaseOpen.Wait(); return new MemoryStream();
            }, stream =>
            {
                cleanupStarted.TrySetResult(); releaseCleanup.Wait(); stream.Dispose();
            }, cancellation.Token);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => opening);
                preparation.Dispose();
                releaseOpen.Set();
                await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await ThrowsAsync<TimeoutException>(() => NasPreparation.AcquireAsync(host, false, "Do not overlap cleanup", default,
                    waitTimeout: TimeSpan.FromMilliseconds(25)));
                check(true, "Host gate remains held while a late handle is still being disposed, not only while native open runs");
            }
            finally { preparation.Dispose(); releaseOpen.Set(); releaseCleanup.Set(); }
            using var next = await NasPreparation.AcquireAsync(host, false, "After slow cleanup", default);
            check(true, "New opening becomes possible after delayed handle disposal finishes");
        }
    }
}
