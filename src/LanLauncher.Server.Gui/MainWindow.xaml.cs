using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace LanLauncher.Server.Gui;

public partial class MainWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Process? serverProcess;
    private readonly string serverExe = Path.Combine(AppContext.BaseDirectory, "Server", "LanLauncher.Server.exe");
    private const string BaseUrl = "http://127.0.0.1:8080";

    public MainWindow()
    {
        InitializeComponent();
        UpdateVersionLabel();
        UpdateLanAddress();
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        refreshTimer.Start();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { refreshTimer.Stop(); http.Dispose(); serverProcess?.Dispose(); };
    }

    private string GetStoragePath()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "Server", "appsettings.json");
        string? configured = null;
        if (File.Exists(configPath))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(configPath));
            if (json.RootElement.TryGetProperty("StoragePath", out var property))
                configured = property.GetString();
        }
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "Server", "Storage")
            : configured;
        return Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(AppContext.BaseDirectory, "Server", root));
    }

    private void OpenGamesFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.Combine(GetStoragePath(), "Packages");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Spieleordner öffnen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SyncGames_Click(object sender, RoutedEventArgs e)
    {
        SyncGamesButton.IsEnabled = false;
        try
        {
            if (!await IsRunningAsync())
            {
                MessageBox.Show(this, "Bitte zuerst den Server starten.", "Spiele synchronisieren");
                return;
            }
            var key = Environment.GetEnvironmentVariable("LANLAUNCHER_AdminKey");
            if (string.IsNullOrWhiteSpace(key))
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "Server", "appsettings.json");
                if (File.Exists(configPath))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(configPath));
                    if (json.RootElement.TryGetProperty("AdminKey", out var property))
                        key = property.GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show(this, "Für die Synchronisierung muss ein AdminKey in Server/appsettings.json oder als Umgebungsvariable LANLAUNCHER_AdminKey konfiguriert sein.", "Administratorzugriff erforderlich");
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/admin/library/pending");
            request.Headers.Add("X-Admin-Key", key);
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                MessageBox.Show(this, "AdminKey ungültig. Bitte Serverkonfiguration prüfen.", "Synchronisierung");
                return;
            }
            response.EnsureSuccessStatusCode();
            var pending = await response.Content.ReadFromJsonAsync<string[]>() ?? [];
            var registered = 0;
            foreach (var filename in pending)
            {
                var dialog = new RegisterGameWindow(filename) { Owner = this };
                if (dialog.ShowDialog() != true) continue;
                var payload = new
                {
                    FileName = filename,
                    Name = dialog.GameName,
                    Version = dialog.GameVersion,
                    Description = dialog.GameDescription,
                    Tags = new { PlayerCounts = dialog.PlayerCounts, Genres = dialog.SelectedGenres }
                };
                using var registration = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/admin/library/register");
                registration.Headers.Add("X-Admin-Key", key);
                registration.Content = JsonContent.Create(payload);
                using var saved = await http.SendAsync(registration);
                if (!saved.IsSuccessStatusCode)
                {
                    MessageBox.Show(this, "Spiel konnte nicht registriert werden: " + filename +
                        "\\n" + await saved.Content.ReadAsStringAsync(), "Synchronisierung");
                    continue;
                }
                registered++;
            }
            await RefreshAsync();
            MessageBox.Show(this, registered + " neue Spiele registriert. " +
                (pending.Length - registered) + " Pakete bleiben zur späteren Registrierung offen.",
                "Synchronisierung abgeschlossen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Synchronisierung fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SyncGamesButton.IsEnabled = true; }
    }

    private void UpdateVersionLabel()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "launcher-version.txt");
            var version = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            ServerVersionText.Text = System.Text.RegularExpressions.Regex.IsMatch(version, @"^v[0-9]+\.[0-9]+\.[0-9]+$")
                ? version : "Entwicklungsversion";
        }
        catch (IOException) { ServerVersionText.Text = "Version unbekannt"; }
        catch (UnauthorizedAccessException) { ServerVersionText.Text = "Version unbekannt"; }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Prüfe ...";
        try
        {
            var (release, message) = await ServerUpdater.CheckAsync();
            if (release is null)
            {
                MessageBox.Show(this, message, "Server-Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this, message + "\n\nJetzt herunterladen und installieren? Die Server-GUI wird neu gestartet und der Server währenddessen gestoppt.",
                "Server-Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (await IsRunningAsync() && serverProcess is not { HasExited: false })
            {
                MessageBox.Show(this, "Es läuft ein Server, der nicht über diese GUI gestartet wurde. Bitte diesen Server vor dem Update beenden.",
                    "Server-Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            UpdateButton.Content = "Lade Update ...";
            await ServerUpdater.InstallAsync(release, Environment.ProcessId,
                serverProcess is { HasExited: false } ? serverProcess.Id : null);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
            System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, "Update fehlgeschlagen: " + ex.Message, "Server-Update",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Updates prüfen";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(serverExe))
        {
            MessageBox.Show(this, "Server-Datei fehlt:\n" + serverExe +
                "\nBitte das vollständige Server-ZIP entpacken.", "Server nicht gefunden");
            return;
        }
        if (await IsRunningAsync())
        {
            MessageBox.Show(this, "Port 8080 antwortet bereits. Es wird kein zweiter Server gestartet.",
                "Server läuft bereits");
            await RefreshAsync();
            return;
        }
        try
        {
            serverProcess?.Dispose();
            serverProcess = Process.Start(new ProcessStartInfo(serverExe)
            {
                WorkingDirectory = Path.GetDirectoryName(serverExe)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (serverProcess is null) throw new InvalidOperationException("Serverprozess konnte nicht gestartet werden.");
            StatusText.Text = "Status: Wird gestartet ...";
            await Task.Delay(900);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Serverstart fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (serverProcess is null || serverProcess.HasExited)
        {
            MessageBox.Show(this, "Dieser Server wurde nicht über die Oberfläche gestartet. " +
                "Bitte den extern gestarteten Prozess separat beenden.", "Server stoppen");
            return;
        }
        if (MessageBox.Show(this, "Server wirklich stoppen? Verbundene Clients werden getrennt.",
            "Server stoppen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            serverProcess.Kill(entireProcessTree: true);
            await serverProcess.WaitForExitAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Stoppen fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateLanAddress()
    {
        const int port = 8080;
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address))
            .Where(a => !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        AddressText.Text = addresses.Length == 0
            ? "LAN-IP: nicht gefunden | Port: " + port
            : "LAN-IP: " + string.Join(", ", addresses.Select(a => a.ToString())) +
              " | Port: " + port;
    }

    private async Task<bool> IsRunningAsync()
    {
        try
        {
            using var response = await http.GetAsync(BaseUrl + "/api/health");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private async Task RefreshAsync()
    {
        try
        {
            UpdateLanAddress();
            var online = await IsRunningAsync();
            StatusText.Text = online ? "Status: Online" : "Status: Gestoppt";
            StartButton.IsEnabled = !online;
            StopButton.IsEnabled = online && serverProcess is { HasExited: false };
            if (!online)
            {
                PlayersList.ItemsSource = null;
                GamesList.ItemsSource = null;
                PlayerCountText.Text = "0 Spieler online";
                GameCountText.Text = "0 Spiele";
                return;
            }
            using var players = await http.GetAsync(BaseUrl + "/api/players");
            using var games = await http.GetAsync(BaseUrl + "/api/games");
            players.EnsureSuccessStatusCode();
            games.EnsureSuccessStatusCode();
            var playerData = await players.Content.ReadFromJsonAsync<List<PlayerRow>>() ?? new();
            var gameData = await games.Content.ReadFromJsonAsync<List<GameRow>>() ?? new();
            PlayersList.ItemsSource = playerData;
            GamesList.ItemsSource = gameData;
            PlayerCountText.Text = playerData.Count + " Spieler online";
            GameCountText.Text = gameData.Count + " Spiele verfügbar";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            FooterText.Text = "Aktualisierung fehlgeschlagen: " + ex.Message;
        }
    }

    private sealed record PlayerRow(string Name, string Status);
    private sealed record GameRow(string Name, string Version);
}
