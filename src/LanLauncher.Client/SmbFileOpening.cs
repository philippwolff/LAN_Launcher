using System.Diagnostics;
using LanLauncher.Client.Logging;

namespace LanLauncher.Client;

/// <summary>One native open, a delayed notice, and cancellation with late handle cleanup.</summary>
internal static class SmbFileOpening
{
    internal const string WaitingMessage = "NAS-Datei wird geöffnet. Die Windows-Sicherheitsprüfung kann etwas dauern.";

    internal static async Task<T> WaitAsync<T>(Func<T> open, Action<T> discard, CancellationToken token,
        Action<bool>? waitingChanged, Action<Task> trackWork, Func<string> description, TimeSpan? noticeAfter = null)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var delivered = false;
        var noticeShown = false;
        var noticeAt = TimeSpan.Zero;
        var work = Task.Run(() => { token.ThrowIfCancellationRequested(); return open(); });
        trackWork(work);
        using var noticeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var notice = Task.Delay(noticeAfter ?? TimeSpan.FromSeconds(15), noticeCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(work, notice).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (completed == notice && !work.IsCompleted)
            {
                noticeShown = true;
                noticeAt = clock.Elapsed;
                ClientLog.Write(LogSeverity.WARN, "SMB", $"Dateiöffnung läuft seit {clock.Elapsed.TotalSeconds:0.000} s; nur Wartehinweis, kein neuer SMB-Aufruf. " + description());
                Notify(waitingChanged, true);
            }
            // No timeout/retry here: continuing to wait always refers to this exact task.
            var result = await work.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            delivered = true;
            ClientLog.Write(LogSeverity.INFO, "SMB", $"Dateiöffnung erfolgreich nach {clock.Elapsed.TotalSeconds:0.000} s; zusätzliche Wartezeit nach Hinweis " +
                $"{(noticeShown ? (clock.Elapsed - noticeAt).TotalSeconds : 0):0.000} s; Download kann beginnen. " + description());
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ClientLog.Write(LogSeverity.WARN, "SMB", $"Downloadauftrag während der Dateiöffnung nach {clock.Elapsed.TotalSeconds:0.000} s abgebrochen; laufender Windows-Aufruf wird kontrolliert aufgeräumt. " + description());
            throw;
        }
        finally
        {
            noticeCancellation.Cancel();
            if (!delivered)
            {
                // The host gate tracks both the native task and this cleanup, even after
                // the UI has abandoned its await. Never detach a destination writer.
                var cleanup = work.ContinueWith(done =>
                {
                    try
                    {
                        if (done.Status == TaskStatus.RanToCompletion)
                        {
                            discard(done.Result);
                            ClientLog.Write(LogSeverity.INFO, "SMB", $"Verspätetes Datei-Handle nach {clock.Elapsed.TotalSeconds:0.000} s geschlossen. " + description());
                        }
                        else if (done.IsFaulted)
                            ClientLog.Write(token.IsCancellationRequested && done.Exception?.GetBaseException() is OperationCanceledException
                                    ? LogSeverity.INFO : LogSeverity.ERROR,
                                "SMB", $"Dateiöffnung ohne Übergabe nach {clock.Elapsed.TotalSeconds:0.000} s beendet. " + description(), done.Exception);
                    }
                    catch (Exception error)
                    { ClientLog.Write(LogSeverity.ERROR, "SMB", "Verspätetes Datei-Handle konnte nicht ordnungsgemäß geschlossen werden.", error); }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                trackWork(cleanup);
            }
            if (noticeShown) Notify(waitingChanged, false);
        }
    }

    private static void Notify(Action<bool>? notification, bool waiting)
    {
        try { notification?.Invoke(waiting); }
        catch (Exception error) { ClientLog.Write(LogSeverity.WARN, "SMB", "Warteanzeige konnte nicht aktualisiert werden.", error); }
    }
}
