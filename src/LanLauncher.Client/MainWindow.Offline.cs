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
        }).Where(game => !settings.HiddenGameIds.Contains(game.Id))
          // Older local and server catalog IDs can point to the same installation.
          // Keep one visible entry per game name, without deleting stored records.
          .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
          .Select(group => group
              .OrderByDescending(g => settings.InstalledExecutables.TryGetValue(g.Id, out var exe)
                  && !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
              .ThenBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
              .First())
          .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
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
