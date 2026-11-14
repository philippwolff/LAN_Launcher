using System.Windows;

namespace LanLauncher.Client;

public partial class UninstallProgressWindow : Window
{
    private readonly string? folder;
    private readonly string? uninstaller;
    private readonly Action onCompleted;
    private bool running;

    public UninstallProgressWindow(string gameName, string description,
        string? folder, string? uninstaller, Action onCompleted)
    {
        InitializeComponent();
        Heading.Text = "Deinstallation: " + gameName;
        Description.Text = description;
        this.folder = folder;
        this.uninstaller = uninstaller;
        this.onCompleted = onCompleted;
        Progress.Value = 0;
        Detail.Text = "Bitte die Deinstallation bestätigen.";
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (running) return;
        running = true;
        ConfirmButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        CloseButton.Content = "Schließen";
        try
        {
            if (uninstaller is not null)
            {
                Detail.Text = "Deinstallationsassistent läuft. Bitte dessen Anweisungen folgen.";
                Progress.IsIndeterminate = true;
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uninstaller)
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(uninstaller)!
                }) ?? throw new InvalidOperationException("Deinstallationsprogramm konnte nicht gestartet werden.");
                await process.WaitForExitAsync();
                Progress.IsIndeterminate = false;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Der Deinstallationsassistent wurde mit Code " + process.ExitCode + " beendet.");
                Detail.Text = "Deinstallationsassistent beendet. Bitte prüfen, ob das Spiel entfernt wurde.";
            }
            else if (folder is not null)
            {
                Detail.Text = "Installationsordner wird gelöscht ...";
                Progress.IsIndeterminate = true;
                await Task.Run(() => System.IO.Directory.Delete(folder, recursive: true));
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                onCompleted();
                Detail.Text = "Deinstallation abgeschlossen. Der lokale Spielordner wurde entfernt.";
            }
            CloseButton.Content = "Schließen";
        }
        catch (Exception ex)
        {
            Progress.IsIndeterminate = false;
            Detail.Text = "Deinstallation fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            running = false;
            CloseButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!running) Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (running) e.Cancel = true;
        base.OnClosing(e);
    }
}