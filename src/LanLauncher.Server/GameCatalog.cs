using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanLauncher.Shared;

namespace LanLauncher.Server;

public sealed class GameCatalog
{
    private readonly string root;
    private readonly string metadataPath;
    private readonly object gate = new();
    private readonly Dictionary<string, GameInfo> games;
    private readonly Dictionary<string, string> launchPaths;
    private readonly string launchPathsFile;
    public string Inbox { get; }

    public GameCatalog(IConfiguration configuration)
    {
        root = Path.GetFullPath(configuration["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Inbox = Path.Combine(root, "Inbox");
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        metadataPath = Path.Combine(root, "games.json");
        launchPathsFile = Path.Combine(root, "launch-paths.json");
        launchPaths = File.Exists(launchPathsFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(launchPathsFile)) ?? new() : new();
        games = File.Exists(metadataPath)
            ? JsonSerializer.Deserialize<Dictionary<string, GameInfo>>(File.ReadAllText(metadataPath)) ?? new()
            : new();
    }

    public void ReloadFromDisk()
    {
        lock (gate)
        {
            var loaded = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<Dictionary<string, GameInfo>>(File.ReadAllText(metadataPath)) ?? new()
                : new Dictionary<string, GameInfo>();
            games.Clear();
            foreach (var entry in loaded) games[entry.Key] = entry.Value;
        }
    }

    public GameInfo[] List()
    {
        lock (gate)
        {
            var packageFolder = Path.Combine(root, "Packages");
            return games.Values
                .Where(g => GamePackagePaths.Find(packageFolder, g.Id) is not null)
                .OrderBy(g => g.Name)
                .ToArray();
        }
    }

    public GameInfo Import(string sourceName, string name, string version, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Name und Version sind erforderlich.");
        var file = Path.GetFileName(sourceName);
        if (file != sourceName || string.IsNullOrWhiteSpace(file) || !new[] { ".zip", ".exe" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
            throw new ArgumentException("Nur ZIP- und EXE-Dateien im Inbox-Ordner werden unterstützt.");
        var source = Path.Combine(Inbox, file);
        if (!File.Exists(source)) throw new FileNotFoundException("Datei nicht im Inbox-Ordner gefunden.");
        var id = CreateGameId(name);
        var game = new GameInfo(id, name.Trim(), version.Trim(), description);
        var target = Path.Combine(root, "Packages", GamePackagePaths.Filename(game, Path.GetExtension(file).ToLowerInvariant()));
        File.Copy(source, target);
        lock (gate) { games[id] = game; Save(); }
        return game;
    }

    public GameInfo Upload(Stream input, string originalFileName, string name, string version, string? description)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (ext is not (".zip" or ".exe")) throw new ArgumentException("Nur ZIP und EXE erlaubt.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Name und Version erforderlich.");
        var id = CreateGameId(name);
        var game = new GameInfo(id, name.Trim(), version.Trim(), description);
        var target = Path.Combine(root, "Packages", GamePackagePaths.Filename(game, ext));
        using (var output = File.Create(target)) input.CopyTo(output);
        lock (gate) { games[id] = game; Save(); }
        return game;
    }

    private static string CreateGameId(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) ==
                System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(ch);
            else if (ch is 'ä') builder.Append("ae");
            else if (ch is 'ö') builder.Append("oe");
            else if (ch is 'ü') builder.Append("ue");
            else if (ch is 'ß') builder.Append("ss");
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        if (slug.Length == 0) slug = "spiel";
        return slug + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    public (string Path, string Name)? Package(string id)
    {
        var folder = Path.Combine(root, "Packages");
        lock (gate) { if (!games.ContainsKey(id)) return null; }
        var path = GamePackagePaths.Find(folder, id);
        return path is null ? null : (path, Path.GetFileName(path));
    }

    public string? GetLaunchPath(string id)
    {
        lock (gate) return games.ContainsKey(id) && launchPaths.TryGetValue(id, out var path) ? path : null;
    }

    public void SetLaunchPath(string id, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Split(new[] { '/', '\\' }).Any(x => x is ".." or "." or "") ||
            !relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Nur relative EXE-Pfade innerhalb des Spielordners erlaubt.");
        lock (gate)
        {
            if (!games.ContainsKey(id)) throw new KeyNotFoundException("Spiel nicht gefunden.");
            launchPaths[id] = relativePath.Replace('/', '\\');
            var tmp = launchPathsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(launchPaths));
            File.Move(tmp, launchPathsFile, true);
        }
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            if (!games.Remove(id)) return false;
            Save();
        }
        var package = Path.Combine(root, "Packages");
        foreach (var path in Directory.GetFiles(package).Where(p =>
            Path.GetFileNameWithoutExtension(p) == id ||
            Path.GetFileNameWithoutExtension(p).EndsWith("--" + id, StringComparison.Ordinal)))
            File.Delete(path);
        return true;
    }

    private void Save()
    {
        var temp = metadataPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, metadataPath, true);
    }
}
