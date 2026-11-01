using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private GameTags pendingTags = new([], []);

    private async void AddTags_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || string.IsNullOrWhiteSpace(AdminKeyBox.Password))
        {
            MessageBox.Show(this, "Bitte zuerst verbinden und einen Admin-Schlüssel eingeben.",
                "Admin erforderlich", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            using var check = AdminRequest(HttpMethod.Get, "/api/admin/check");
            using var response = await http.SendAsync(check);
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show(this, "Ungültiger Admin-Schlüssel.", "Admin erforderlich",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Admin-Prüfung", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var window = new GameTagsWindow("Tags für neues Spiel", pendingTags) { Owner = this };
        if (window.ShowDialog() == true) pendingTags = window.Tags;
    }

    private async Task SaveTagsAsync(string gameId, GameTags tags)
    {
        using var request = AdminRequest(HttpMethod.Put,
            "/api/admin/games/" + Uri.EscapeDataString(gameId) + "/tags");
        request.Content = JsonContent.Create(tags);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Tags nicht gespeichert: HTTP " + (int)response.StatusCode +
                ". Bitte Admin-Schlüssel und Server-Version prüfen.");
    }

    private async void EditTags(GameInfo game)
    {
        if (server is null || string.IsNullOrWhiteSpace(AdminKeyBox.Password))
        {
            MessageBox.Show(this, "Zum Bearbeiten der Tags ist eine Verbindung und ein Admin-Schlüssel erforderlich.",
                "Admin erforderlich", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            using var check = AdminRequest(HttpMethod.Get, "/api/admin/check");
            using var checkResponse = await http.SendAsync(check);
            if (!checkResponse.IsSuccessStatusCode)
            {
                MessageBox.Show(this, "Ungültiger Admin-Schlüssel.", "Admin erforderlich",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            using var tagsResponse = await http.GetAsync(
                server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/tags");
            if (tagsResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                MessageBox.Show(this,
                    "Der Server bietet für dieses Spiel noch keine Tag-Verwaltung an.\n\n" +
                    "Mögliche Ursache: Auf Port 8080 läuft noch eine ältere Serverversion.\n" +
                    "Bitte den bisherigen Server beenden und Server-starten.cmd erneut ausführen.\n" +
                    "Prüfe außerdem, ob das Spiel noch auf dem Server vorhanden ist.",
                    "Tag-Verwaltung nicht verfügbar", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            tagsResponse.EnsureSuccessStatusCode();
            var current = await tagsResponse.Content.ReadFromJsonAsync<GameTags>();
            var dialog = new GameTagsWindow(game.Name, current) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await SaveTagsAsync(game.Id, dialog.Tags);
            StatusText.Text = "Tags gespeichert: " + game.Name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tags bearbeiten", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
