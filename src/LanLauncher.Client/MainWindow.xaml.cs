using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Net.Http.Headers;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using LanLauncher.Shared;

namespace LanLauncher.Client;
public partial class MainWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly string clientId = Guid.NewGuid().ToString("N");
    private string? server;
    public MainWindow()
    {
        InitializeComponent();
        timer.Tick += async (_, _) => await RefreshAsync();
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var address = ServerBox.Text.Trim();
            if (!address.StartsWith("http://") && !address.StartsWith("https://")) address = "http://" + address;
            server = address.TrimEnd('/');
            var name = NameBox.Text.Trim();
            if (name.Length is < 2 or > 32) { StatusText.Text = "Benutzername: 2–32 Zeichen."; return; }
            var response = await http.PostAsJsonAsync(server + "/api/join", new JoinRequest(name, clientId));
            if (!response.IsSuccessStatusCode) { StatusText.Text = "Anmeldung fehlgeschlagen: " + await response.Content.ReadAsStringAsync(); timer.Stop(); return; }
            StatusText.Text = "Verbunden als " + name;
            timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex) { timer.Stop(); StatusText.Text = "Verbindungsfehler: " + ex.Message; }
    }
    private async Task RefreshAsync()
    {
        if (server is null) return;
        try
        {
            await http.PostAsJsonAsync(server + "/api/status", new StatusRequest(clientId, "Online", null));
            var games = await http.GetFromJsonAsync<GameInfo[]>(server + "/api/games") ?? [];
            var players = await http.GetFromJsonAsync<PlayerInfo[]>(server + "/api/players") ?? [];
            GamesList.ItemsSource = games;
            PlayersList.ItemsSource = players.Select(p => p.Name + " — " + p.Status + (p.GameId is null ? "" : " (" + p.GameId + ")")).ToArray();
            StatusText.Text = "Verbunden — " + players.Length + " Spieler online";
        }
        catch (Exception ex) { StatusText.Text = "Server nicht erreichbar: " + ex.Message; }
    }
    private HttpRequestMessage AdminRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, server + endpoint);
        request.Headers.Add("X-Admin-Key", AdminKeyBox.Password);
        return request;
    }
    private async void Inbox_Click(object sender, RoutedEventArgs e)
    {
        if (server is null) { StatusText.Text = "Zuerst verbinden."; return; }
        try
        {
            using var request = AdminRequest(HttpMethod.Get, "/api/admin/inbox");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { StatusText.Text = "Admin-Zugriff verweigert: " + response.StatusCode; return; }
            InboxFiles.ItemsSource = await response.Content.ReadFromJsonAsync<string[]>() ?? [];
            StatusText.Text = "Server-Inbox aktualisiert.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || InboxFiles.SelectedItem is not string file) { StatusText.Text = "Verbinden und Datei aus der Inbox wählen."; return; }
        try
        {
            using var request = AdminRequest(HttpMethod.Post, "/api/admin/import");
            request.Content = JsonContent.Create(new { fileName = file, name = GameNameBox.Text, version = GameVersionBox.Text, description = "" });
            using var response = await http.SendAsync(request);
            StatusText.Text = response.IsSuccessStatusCode ? "Spiel importiert." : "Import fehlgeschlagen: " + await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) await RefreshAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (server is null) { StatusText.Text = "Zuerst verbinden."; return; }
        try
        {
            using var checkRequest = AdminRequest(HttpMethod.Get, "/api/admin/check");
            using var checkResponse = await http.SendAsync(checkRequest);
            if (!checkResponse.IsSuccessStatusCode)
            {
                StatusText.Text = "Upload abgelehnt: Admin-Schlüssel fehlt oder ist ungültig (HTTP " + (int)checkResponse.StatusCode + ").";
                return;
            }
            if (string.IsNullOrWhiteSpace(GameNameBox.Text) || string.IsNullOrWhiteSpace(GameVersionBox.Text))
            {
                StatusText.Text = "Bitte Spielname und Version eingeben.";
                return;
            }
        }
        catch (Exception ex) { StatusText.Text = "Admin-Prüfung fehlgeschlagen: " + ex.Message; return; }
        var dialog = new OpenFileDialog { Filter = "Spielepakete (*.zip;*.exe)|*.zip;*.exe" };
        if (dialog.ShowDialog() != true) return;
        var popup = new TransferProgressWindow("Upload: " + Path.GetFileName(dialog.FileName)) { Owner = this };
        popup.Show();
        try
        {
            using var request = AdminRequest(HttpMethod.Post, "/api/admin/upload");
            using var multipart = new MultipartFormDataContent();
            using var file = File.OpenRead(dialog.FileName);
            using var stream = new ProgressStreamContent(file,
                (sent, total) => Dispatcher.InvokeAsync(() => popup.Update(sent, total)), popup.Cancellation);
            stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(stream, "file", Path.GetFileName(dialog.FileName));
            multipart.Add(new StringContent(GameNameBox.Text), "name");
            multipart.Add(new StringContent(GameVersionBox.Text), "version");
            request.Content = multipart;
            using var uploadClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            StatusText.Text = "Upload läuft …";
            using var response = await uploadClient.SendAsync(request, popup.Cancellation);
            popup.Finishing();
            StatusText.Text = response.IsSuccessStatusCode ? "Spiel hochgeladen." : "Upload fehlgeschlagen (HTTP " + (int)response.StatusCode + "): " + await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) await RefreshAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Upload abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Upload fehlgeschlagen: " + ex.Message; }
        finally { popup.Close(); }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || GamesList.SelectedItem is not GameInfo game) { StatusText.Text = "Bitte ein Spiel auswählen."; return; }
        var popup = new TransferProgressWindow("Download: " + game.Name) { Owner = this };
        popup.Show();
        string? outputPath = null;
        try
        {
            using var response = await http.GetAsync(server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/package",
                HttpCompletionOption.ResponseHeadersRead, popup.Cancellation);
            response.EnsureSuccessStatusCode();
            var suggested = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? (game.Name + ".zip");
            var dialog = new SaveFileDialog { FileName = suggested, Filter = "Alle Dateien|*.*" };
            if (dialog.ShowDialog() != true) { StatusText.Text = "Download abgebrochen."; return; }
            outputPath = dialog.FileName;
            await using var input = await response.Content.ReadAsStreamAsync(popup.Cancellation);
            await using var output = File.Create(outputPath);
            var total = response.Content.Headers.ContentLength;
            var buffer = new byte[128 * 1024];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, popup.Cancellation)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), popup.Cancellation);
                received += count;
                popup.Update(received, total);
            }
            await output.FlushAsync(popup.Cancellation);
            StatusText.Text = "Paket gespeichert: " + outputPath;
            outputPath = null;
        }
        catch (OperationCanceledException) { StatusText.Text = "Download abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Download fehlgeschlagen: " + ex.Message; }
        finally
        {
            if (outputPath is not null && File.Exists(outputPath))
                try { File.Delete(outputPath); } catch (IOException) { }
            popup.Close();
        }
    }

}
