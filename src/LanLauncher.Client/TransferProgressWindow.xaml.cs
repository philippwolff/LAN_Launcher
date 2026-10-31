using System.Windows;

namespace LanLauncher.Client;

public partial class TransferProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
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
    public void Finishing() { TransferBar.IsIndeterminate = true; TransferDetails.Text = "Übertragung abgeschlossen – Server verarbeitet Datei …"; CancelButton.IsEnabled = false; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; cancellation.Cancel(); TransferDetails.Text = "Abbruch läuft …"; }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (CancelButton.IsEnabled) { e.Cancel = true; cancellation.Cancel(); CancelButton.IsEnabled = false; TransferDetails.Text = "Abbruch läuft …"; }
        base.OnClosing(e);
    }
}
