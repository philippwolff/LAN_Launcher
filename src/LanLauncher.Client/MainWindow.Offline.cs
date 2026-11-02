using System.IO;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private void ShowOfflineLibrary(string message)
    {
        var selectedId = (GamesList.SelectedItem as GameInfo)?.Id ?? settings.LastOpenedGameId;
        var localGames = settings.KnownGames.Values.Where(game =>
        {
            if (settings.InstalledExecutables.TryGetValue(game.Id, out var exe) &&
                !string.IsNullOrWhiteSpace(exe) && File.Exists(exe)) return true;
            var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
            return GameExecutableFinder.FindBest(folder, game.Name) is not null;
        }).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        GameDisplayNameConverter.LocalOnlyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        GameNotInstalledConverter.ServerGameIds =
            localGames.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InitializeTagFilters();
        allLibraryGames = localGames;
        ApplyLibraryTagFilters(selectedId);
        PlayersList.ItemsSource = Array.Empty<string>();
        StatusText.Text = message + " (" + localGames.Length + " Spiele)";
    }
}
