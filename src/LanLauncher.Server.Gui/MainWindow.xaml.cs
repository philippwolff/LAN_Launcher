using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        refreshTimer.Start();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { refreshTimer.Stop(); http.Dispose(); serverProcess?.Dispose(); };
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
