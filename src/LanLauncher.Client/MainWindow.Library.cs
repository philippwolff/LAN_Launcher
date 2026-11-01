using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using Microsoft.Win32;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private async Task<bool> CheckLibraryAdminAsync()
    {
        if (server is null || string.IsNullOrWhiteSpace(AdminKeyBox.Password))
        {
            MessageBox.Show(this, "Bitte zuerst verbinden und einen Admin-Schlüssel eingeben.",
                "Serverbibliothek", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        using var request = AdminRequest(HttpMethod.Get, "/api/admin/check");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            MessageBox.Show(this, "Der Admin-Schlüssel ist ungültig.", "Serverbibliothek");
            return false;
        }
        return true;
    }

    private async void LibraryCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CheckLibraryAdminAsync()) return;
            using var request = AdminRequest(HttpMethod.Get, "/api/admin/library/check");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var check = await response.Content.ReadFromJsonAsync<LibraryCheckResult>();
            if (check is null) return;
            var message = "Spiele im Katalog: " + check.Games +
                "\nPakete auf dem Server: " + check.Packages +
                "\nFehlende Pakete: " + check.MissingPackages +
                "\nPakete ohne Katalogeintrag: " + check.UnregisteredPackages;
            if (check.UnregisteredPackages == 0)
            {
                MessageBox.Show(this, message, "Serverbibliothek geprüft");
                return;
            }
            if (MessageBox.Show(this, message +
                "\n\nVerwaiste Pakete mit Platzhalternamen wieder registrieren?\n" +
                "Der Server muss anschließend neu gestartet werden.",
                "Serverbibliothek prüfen", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            using var repairRequest = AdminRequest(HttpMethod.Post, "/api/admin/library/check");
            using var repairResponse = await http.SendAsync(repairRequest);
            repairResponse.EnsureSuccessStatusCode();
            var repaired = await repairResponse.Content.ReadFromJsonAsync<LibraryCheckResult>();
            MessageBox.Show(this, (repaired?.Recovered ?? 0) + " Pakete registriert.\n" +
                "Bitte Server neu starten, damit der Katalog neu geladen wird.",
                "Serverbibliothek geprüft");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Serverbibliothek prüfen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LibraryTransfer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CheckLibraryAdminAsync()) return;
            var action = MessageBox.Show(this,
                "Serverbibliothek exportieren oder importieren?\n\n" +
                "Ja = Gesamte Serverbibliothek als ZIP exportieren\n" +
                "Nein = Vorhandene Serverbibliothek aus ZIP importieren\n" +
                "Abbrechen = Schließen",
                "Serverbibliothek Im-/Export", MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (action == MessageBoxResult.Yes) await ExportLibraryAsync();
            else if (action == MessageBoxResult.No) await ImportLibraryAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Serverbibliothek", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportLibraryAsync()
    {
        var picker = new SaveFileDialog
        {
            Title = "Serverbibliothek exportieren",
            Filter = "ZIP-Archiv (*.zip)|*.zip",
            FileName = "LANLauncher-Serverbibliothek.zip"
        };
        if (picker.ShowDialog(this) != true) return;
        var popup = new TransferProgressWindow("Serverbibliothek exportieren") { Owner = this };
        activeTransfers.Add(popup);
        popup.Show();
        try
        {
            using var request = AdminRequest(HttpMethod.Get, "/api/admin/library/export");
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, popup.Cancellation);
            response.EnsureSuccessStatusCode();
            var partial = picker.FileName + ".part";
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(popup.Cancellation);
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write,
                    FileShare.None, 128 * 1024, useAsync: true))
                {
                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, popup.Cancellation)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), popup.Cancellation);
                        received += count;
                        popup.Update(received, response.Content.Headers.ContentLength);
                    }
                }
                File.Move(partial, picker.FileName, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
            StatusText.Text = "Serverbibliothek exportiert: " + picker.FileName;
        }
        finally { activeTransfers.Remove(popup); popup.CloseAfterTransfer(); }
    }

    private async Task ImportLibraryAsync()
    {
        var picker = new OpenFileDialog
        {
            Title = "Serverbibliothek importieren",
            Filter = "ZIP-Archiv (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
            "ACHTUNG: Die importierte Bibliothek ersetzt die Spiel-Metadaten auf dem Server.\n" +
            "Bestehende Serverdaten werden vorher im Backup-Ordner gesichert.\n\n" +
            "Der Server muss nach dem Import neu gestartet werden.\n\nFortfahren?",
            "Serverbibliothek importieren", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var popup = new TransferProgressWindow("Serverbibliothek importieren") { Owner = this };
        activeTransfers.Add(popup);
        popup.Show();
        try
        {
            using var request = AdminRequest(HttpMethod.Post, "/api/admin/library/import");
            using var stream = File.OpenRead(picker.FileName);
            using var multipart = new MultipartFormDataContent();
            using var upload = new ProgressStreamContent(stream,
                (sent, total) => Dispatcher.InvokeAsync(() => popup.Update(sent, total)), popup.Cancellation);
            upload.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            multipart.Add(upload, "file", Path.GetFileName(picker.FileName));
            request.Content = multipart;
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.SendAsync(request, popup.Cancellation);
            if (!response.IsSuccessStatusCode)
                throw new IOException("Import fehlgeschlagen: " + await response.Content.ReadAsStringAsync());
            MessageBox.Show(this, "Serverbibliothek importiert.\n" +
                "Bitte den Server jetzt neu starten. Danach wird die Spieleliste aktualisiert.",
                "Import abgeschlossen");
            StatusText.Text = "Serverbibliothek importiert – Server-Neustart erforderlich.";
        }
        finally { activeTransfers.Remove(popup); popup.CloseAfterTransfer(); }
    }

    private sealed record LibraryCheckResult(int Games, int Packages, int MissingPackages,
        int Recovered, int UnregisteredPackages);
}
