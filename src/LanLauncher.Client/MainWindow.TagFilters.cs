using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private GameInfo[] allLibraryGames = [];
    private readonly Dictionary<string, GameTags> libraryTags = new(StringComparer.OrdinalIgnoreCase);
    private bool tagFilterReady;

    private void InitializeTagFilters()
    {
        if (tagFilterReady) return;
        tagFilterReady = true;
        PlayerFilter.Items.Add("Alle Spielerzahlen");
        for (var count = 1; count <= 16; count++)
            PlayerFilter.Items.Add(count + (count == 1 ? " Spieler" : " Spieler"));
        GenreFilter.Items.Add("Alle Genres");
        foreach (var genre in GameTagOptions.Genres) GenreFilter.Items.Add(genre);
        PlayerFilter.SelectedIndex = 0;
        GenreFilter.SelectedIndex = 0;
    }

    private async Task LoadLibraryTagsAsync(GameInfo[] onlineGames)
    {
        if (server is null) return;
        foreach (var game in onlineGames)
        {
            try
            {
                using var response = await http.GetAsync(server + "/api/games/" +
                    Uri.EscapeDataString(game.Id) + "/tags");
                if (response.IsSuccessStatusCode)
                    libraryTags[game.Id] = await response.Content.ReadFromJsonAsync<GameTags>() ??
                        new GameTags([], []);
            }
            catch (Exception) { /* A temporarily unreachable tag service must not hide games. */ }
        }
    }

    private void ApplyLibraryTagFilters(string? preferredId = null)
    {
        if (!tagFilterReady) return;
        var playerCount = PlayerFilter.SelectedIndex > 0 ? PlayerFilter.SelectedIndex : 0;
        var genre = GenreFilter.SelectedIndex > 0 ? GenreFilter.SelectedItem as string : null;
        var currentId = preferredId ?? (GamesList.SelectedItem as GameInfo)?.Id;
        var visible = allLibraryGames.Where(game =>
        {
            if (playerCount == 0 && genre is null) return true;
            if (!libraryTags.TryGetValue(game.Id, out var tags)) return false;
            return (playerCount == 0 || tags.PlayerCounts.Contains(playerCount)) &&
                   (genre is null || tags.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase));
        }).ToArray();
        updatingGames = true;
        try
        {
            GamesList.ItemsSource = visible;
            GamesList.SelectedItem = visible.FirstOrDefault(g => g.Id == currentId);
        }
        finally { updatingGames = false; }
    }

    private void TagFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (tagFilterReady) ApplyLibraryTagFilters();
    }

    private void ResetTagFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!tagFilterReady) return;
        PlayerFilter.SelectedIndex = 0;
        GenreFilter.SelectedIndex = 0;
        ApplyLibraryTagFilters();
    }
}
