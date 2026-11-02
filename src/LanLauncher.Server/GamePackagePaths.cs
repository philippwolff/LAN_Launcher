using LanLauncher.Shared;

namespace LanLauncher.Server;

internal static class GamePackagePaths
{
    internal static string? Find(string folder, string id)
    {
        foreach (var ext in new[] { ".zip", ".exe" })
        {
            var legacy = Path.Combine(folder, id + ext);
            if (File.Exists(legacy)) return legacy;
            var matches = Directory.GetFiles(folder, "*--" + id + ext);
            if (matches.Length > 0) return matches[0];
        }
        return null;
    }

    internal static string Filename(GameInfo game, string ext)
    {
        var name = new string(game.Name.Trim().Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '-' : ch).ToArray()).Trim(' ', '.');
        if (name.Length > 80) name = name[..80].Trim();
        if (name.Length == 0) name = "Spiel";
        return name + "--" + game.Id + ext;
    }

    internal static string? IdFromFilename(string filename, IReadOnlyDictionary<string, GameInfo> games)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (games.ContainsKey(stem)) return stem;
        var separator = stem.LastIndexOf("--", StringComparison.Ordinal);
        if (separator >= 0)
        {
            var id = stem[(separator + 2)..];
            if (games.ContainsKey(id)) return id;
        }
        return null;
    }
}
