using System.Windows;

namespace LanLauncher.Client;

public partial class TransferProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private bool allowClose;
    private bool finishing;
    public CancellationToken Cancellation => cancellation.Token;
    public TransferProgressWindow(string title)
    {
        InitializeComponent();
        TransferTitle.Text = title;
        TransferBar.IsIndeterminate = true;
        TransferDetails.Text = "Übertragung wird vorbereitet …";
    }
    public void Update(long bytes, long? total)
    {
        if (finishing) return;
        if (total is > 0)
        {
            var percent = Math.Clamp(bytes * 100.0 / total.Value, 0, 100);
            TransferBar.IsIndeterminate = false;
            TransferBar.Value = percent;
            TransferDetails.Text = $"{percent:0.0} %  –  {bytes / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB";
        }
        else
        {
            TransferBar.IsIndeterminate = true;
            TransferDetails.Text = $"{bytes / 1048576.0:0.0} MB übertragen – Gesamtgröße unbekannt";
        }
    }
    public void Finishing() { finishing = true; TransferBar.IsIndeterminate = true; TransferDetails.Text = "100 % übertragen – warte auf Bestätigung des Servers (bei großen ZIPs kann das dauern) …"; }
    public void CloseAfterTransfer() { allowClose = true; Close(); }
    public void CancelTransfer()
    {
        if (!cancellation.IsCancellationRequested) cancellation.Cancel();
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
