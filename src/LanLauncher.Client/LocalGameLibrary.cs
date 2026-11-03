using System.IO;
using LanLauncher.Shared;

namespace LanLauncher.Client;

internal static class LocalGameLibrary
{
    public static GameInfo[] Merge(GameInfo[] remote, LauncherSettings settings)
    {
        GameNotInstalledConverter.ServerGameIds = remote.Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var game in remote)
            settings.KnownGames[game.Id] = game;
        var remoteIds = remote.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A game previously registered locally can have a different ID from its NAS catalog entry.
        // Prefer the live server entry in the list, without deleting local installation settings.
        var remoteNames = remote.Select(g => g.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = settings.KnownGames.Values.Where(game =>
        {
            if (remoteIds.Contains(game.Id) || remoteNames.Contains(game.Name.Trim())) return false;
            if (settings.InstalledExecutables.TryGetValue(game.Id, out var exe) && File.Exists(exe))
                return true;
            var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
            return GameExecutableFinder.FindBest(folder, game.Name) is not null;
        }).ToArray();
        GameDisplayNameConverter.LocalOnlyIds = local.Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.Save();
        return remote.Concat(local).Where(g => !settings.HiddenGameIds.Contains(g.Id))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
