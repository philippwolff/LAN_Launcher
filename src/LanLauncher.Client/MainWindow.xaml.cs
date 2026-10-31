using System.Net.Http.Json;
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
}
