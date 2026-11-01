using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private sealed record GameStartNotice(string Text);

    private async Task<string> GetGameStartNoticeAsync(GameInfo game)
    {
        if (server is null || GameDisplayNameConverter.LocalOnlyIds.Contains(game.Id)) return "";
        using var response = await http.GetAsync(server + "/api/games/" +
            Uri.EscapeDataString(game.Id) + "/start-notice");
        if (!response.IsSuccessStatusCode) return "";
        return (await response.Content.ReadFromJsonAsync<GameStartNotice>())?.Text ?? "";
    }

    private async void EditStartNotice(GameInfo game)
    {
        try
        {
            if (server is null || string.IsNullOrWhiteSpace(AdminKeyBox.Password))
            {
                MessageBox.Show(this, "Bitte zuerst verbinden und den Admin-Schlüssel eingeben.");
                return;
            }
            using var check = AdminRequest(HttpMethod.Get, "/api/admin/check");
            using var auth = await http.SendAsync(check);
            if (!auth.IsSuccessStatusCode)
            {
                MessageBox.Show(this, "Ungültiger Admin-Schlüssel.", "Starthinweis");
                return;
            }
            var current = await GetGameStartNoticeAsync(game);
            var dialog = new GameStartNoticeWindow(game, current) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            using var request = AdminRequest(HttpMethod.Put,
                "/api/admin/games/" + Uri.EscapeDataString(game.Id) + "/start-notice");
            request.Content = JsonContent.Create(new GameStartNotice(dialog.Notice));
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Speichern fehlgeschlagen: " +
                    response.StatusCode + " – " + await response.Content.ReadAsStringAsync());
            StatusText.Text = string.IsNullOrWhiteSpace(dialog.Notice)
                ? "Starthinweis deaktiviert: " + game.Name
                : "Starthinweis gespeichert: " + game.Name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Starthinweis bearbeiten",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ShowGameStartNoticeAsync(GameInfo game)
    {
        try
        {
            var notice = await GetGameStartNoticeAsync(game);
            if (!string.IsNullOrWhiteSpace(notice))
                MessageBox.Show(this, notice, "Hinweis – " + game.Name,
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception)
        {
            // A missing or unreachable notice must never prevent the game from launching.
        }
    }
}
