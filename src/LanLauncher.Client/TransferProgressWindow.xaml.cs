using System.Windows;
using LanLauncher.Client.Logging;

namespace LanLauncher.Client;

public partial class TransferProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private bool allowClose;
    private bool finishing;
    private readonly System.Diagnostics.Stopwatch speedClock = System.Diagnostics.Stopwatch.StartNew();
    private long lastSampleBytes;
    private double lastSampleSeconds;
    private double transferBytesPerSecond;
    private readonly string transferDirection;
    private long lastQueuedUpdate;
    private bool waitingForOpening;
    private bool transferStarted;
    private readonly string? operationId = ClientLog.OperationId;
    public CancellationToken Cancellation => cancellation.Token;
    public TransferProgressWindow(string title)
    {
        InitializeComponent();
        TransferTitle.Text = title;
        transferDirection = title.StartsWith("Upload:", StringComparison.OrdinalIgnoreCase) ? "Upload" : "Download";
        TransferBar.IsIndeterminate = true;
        TransferDetails.Text = "Übertragung wird vorbereitet …";
    }
    public void Update(long bytes, long? total, bool showDownloadSpeed = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastQueuedUpdate) < 100 && bytes != total) return;
            Interlocked.Exchange(ref lastQueuedUpdate, now);
            Dispatcher.BeginInvoke(() => Update(bytes, total, showDownloadSpeed));
            return;
        }
        if (finishing || allowClose || cancellation.IsCancellationRequested) return;
        waitingForOpening = false;
        ContinueWaitingButton.Visibility = Visibility.Collapsed;
        if (!transferStarted)
        {
            // Security scanning/opening time must not dilute transfer speed or ETA.
            transferStarted = true;
            speedClock.Restart();
            lastSampleBytes = 0;
            lastSampleSeconds = 0;
            transferBytesPerSecond = 0;
        }
        // Measure upload and download alike, using transferred bytes over elapsed time.
        {
            var elapsed = speedClock.Elapsed.TotalSeconds;
            var delta = elapsed - lastSampleSeconds;
            if (delta >= 0.5)
            {
                var instant = Math.Max(0, bytes - lastSampleBytes) / delta;
                transferBytesPerSecond = lastSampleSeconds == 0 ? instant :
                    transferBytesPerSecond * 0.6 + instant * 0.4;
                lastSampleBytes = bytes;
                lastSampleSeconds = elapsed;
            }
        }
        var speedText = $"  –  {transferDirection}: {transferBytesPerSecond / 1_000_000.0:0.00} MB/s";
        if (total is > 0)
        {
            var percent = Math.Clamp(bytes * 100.0 / total.Value, 0, 100);
            TransferBar.IsIndeterminate = false;
            TransferBar.Value = percent;
            TransferDetails.Text = $"{percent:0.0} %  –  {bytes / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB{speedText}";
            TransferDetails.Text += "\nRestzeit: " + FormatRemaining(bytes, total.Value, transferBytesPerSecond);
        }
        else
        {
            TransferBar.IsIndeterminate = true;
            TransferDetails.Text = $"{bytes / 1048576.0:0.0} MB übertragen – Gesamtgröße unbekannt{speedText}";
            TransferDetails.Text += "\nRestzeit: unbekannt";
        }
    }
    public void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetStatus(message)); return; }
        if (finishing || allowClose || cancellation.IsCancellationRequested || waitingForOpening) return;
        TransferBar.IsIndeterminate = true;
        TransferDetails.Text = message;
    }
    public void SetOpeningWait(bool waiting)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetOpeningWait(waiting)); return; }
        var wasWaiting = waitingForOpening;
        waitingForOpening = waiting && !allowClose && !cancellation.IsCancellationRequested && !transferStarted;
        ContinueWaitingButton.Visibility = waitingForOpening ? Visibility.Visible : Visibility.Collapsed;
        if (waitingForOpening)
        {
            TransferBar.IsIndeterminate = true;
            TransferDetails.Text = SmbFileOpening.WaitingMessage;
        }
        else if (wasWaiting && !transferStarted && !allowClose && !cancellation.IsCancellationRequested)
            TransferDetails.Text = "NAS-Dateiöffnung beendet …";
    }

    private void ContinueWaiting_Click(object sender, RoutedEventArgs e)
    {
        if (!waitingForOpening || cancellation.IsCancellationRequested) return;
        ClientLog.Write(LogSeverity.INFO, "SMB", "Benutzer wartet auf dieselbe laufende Dateiöffnung weiter; kein neuer SMB-Aufruf.", operationId: operationId);
        ContinueWaitingButton.Visibility = Visibility.Collapsed;
        TransferDetails.Text = SmbFileOpening.WaitingMessage;
    }

    internal static string FormatRemaining(long bytes, long total, double bytesPerSecond)
    {
        if (bytes >= total) return "00:00";
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return "wird berechnet …";
        var seconds = (total - bytes) / bytesPerSecond;
        if (!double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds - 1) return "unbekannt";
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, seconds)));
        return remaining.TotalDays >= 1 ? remaining.ToString(@"d\.hh\:mm\:ss")
            : remaining.TotalHours >= 1 ? remaining.ToString(@"h\:mm\:ss") : remaining.ToString(@"mm\:ss");
    }
    public void Finishing() { finishing = true; TransferBar.IsIndeterminate = true; TransferDetails.Text = "100 % übertragen – warte auf Bestätigung des Servers (bei großen ZIPs kann das dauern) …"; }
    public void CloseAfterTransfer() { allowClose = true; Close(); }
    public void CancelTransfer()
    {
        if (!cancellation.IsCancellationRequested) cancellation.Cancel();
        waitingForOpening = false;
        ContinueWaitingButton.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = false;
        TransferDetails.Text = "Abbruch läuft …";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelTransfer();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!allowClose) { e.Cancel = true; CancelTransfer(); }
        base.OnClosing(e);
    }
}
