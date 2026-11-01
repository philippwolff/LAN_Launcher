using System.IO.Compression;
using System.Text.Json;
using LanLauncher.Shared;

namespace LanLauncher.Server;

public sealed class ServerLibraryService
{
    private readonly string root;
    private readonly object gate = new();
    private static readonly string[] Metadata = ["games.json", "game-tags.json", "launch-paths.json", "mod-configurations.json", "game-start-notices.json"];

    public ServerLibraryService(IConfiguration config)
    {
        root = Path.GetFullPath(config["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
    }

    public LibraryCheckResult Check(bool repair)
    {
        lock (gate)
        {
            var catalogFile = Path.Combine(root, "games.json");
            var games = File.Exists(catalogFile)
                ? JsonSerializer.Deserialize<Dictionary<string, GameInfo>>(File.ReadAllText(catalogFile)) ?? new()
                : new Dictionary<string, GameInfo>();
            var packageDir = Path.Combine(root, "Packages");
            var packages = Directory.GetFiles(packageDir)
                .Where(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var recovered = 0;
            foreach (var package in packages)
            {
                var id = Path.GetFileNameWithoutExtension(package);
                if (games.ContainsKey(id)) continue;
                if (!repair) continue;
                games[id] = new GameInfo(id, "Wiederhergestellt - " + id, "Unbekannt",
                    "Paket gefunden. Name und Version bitte administrativ prüfen.");
                recovered++;
            }
            if (recovered > 0)
            {
                var temp = catalogFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, catalogFile, true);
            }
            var missing = games.Keys.Count(id =>
                !File.Exists(Path.Combine(packageDir, id + ".zip")) &&
                !File.Exists(Path.Combine(packageDir, id + ".exe")));
            return new LibraryCheckResult(games.Count, packages.Length, missing, recovered,
                packages.Count(p => !games.ContainsKey(Path.GetFileNameWithoutExtension(p))));
        }
    }

    public string Export()
    {
        lock (gate)
        {
            var archivePath = Path.Combine(Path.GetTempPath(), "LANLauncher-Serverbibliothek-" + Guid.NewGuid().ToString("N") + ".zip");
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var name in Metadata)
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) archive.CreateEntryFromFile(path, name, CompressionLevel.NoCompression);
            }
            foreach (var path in Directory.GetFiles(Path.Combine(root, "Packages")))
                archive.CreateEntryFromFile(path, "Packages/" + Path.GetFileName(path), CompressionLevel.NoCompression);
            return archivePath;
        }
    }

    public LibraryCheckResult Import(Stream source)
    {
        // Replace the library as a unit. Back up the existing files first.
        var staging = Path.Combine(Path.GetTempPath(), "LANLauncher-Import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
            {
                long size = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var name = entry.FullName.Replace('\\', '/');
                    var isMetadata = Metadata.Contains(name);
                    var isPackage = name.StartsWith("Packages/", StringComparison.Ordinal) &&
                        name.Split('/').Length == 2 &&
                        (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (!isMetadata && !isPackage)
                        throw new InvalidDataException("Unzulässige Datei im Archiv: " + name);
                    checked { size += entry.Length; }
                    if (size > 200L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("Archiv zu groß.");
                    var destination = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination);
                }
            }
            var gamesPath = Path.Combine(staging, "games.json");
            if (!File.Exists(gamesPath)) throw new InvalidDataException("games.json fehlt im Archiv.");
            var games = JsonSerializer.Deserialize<Dictionary<string, GameInfo>>(File.ReadAllText(gamesPath))
                ?? throw new InvalidDataException("Ungültiger Spielekatalog.");
            foreach (var id in games.Keys)
            {
                if (!Guid.TryParseExact(id, "N", out _))
                    throw new InvalidDataException("Ungültige Spiel-ID im Katalog.");
                if (!File.Exists(Path.Combine(staging, "Packages", id + ".zip")) &&
                    !File.Exists(Path.Combine(staging, "Packages", id + ".exe")))
                    throw new InvalidDataException("Paket fehlt für Spiel-ID " + id);
            }
            foreach (var name in Metadata.Where(n => n != "games.json"))
            {
                var path = Path.Combine(staging, name);
                if (File.Exists(path))
                    _ = JsonDocument.Parse(File.ReadAllText(path));
            }
            lock (gate)
            {
                var backup = Path.Combine(root, "Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") +
                    "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(backup);
                var packages = Path.Combine(root, "Packages");
                foreach (var name in Metadata)
                {
                    var path = Path.Combine(root, name);
                    if (File.Exists(path)) File.Copy(path, Path.Combine(backup, name));
                }
                var backupPackages = Path.Combine(backup, "Packages");
                Directory.CreateDirectory(backupPackages);
                foreach (var path in Directory.GetFiles(packages))
                    File.Copy(path, Path.Combine(backupPackages, Path.GetFileName(path)));
                // Import is additive for packages; existing games remain in the backup.
                // The new metadata replaces old metadata. Restart server to reload in-memory catalogs.
                foreach (var path in Directory.GetFiles(Path.Combine(staging, "Packages")))
                    File.Copy(path, Path.Combine(packages, Path.GetFileName(path)), true);
                foreach (var name in Metadata)
                {
                    var from = Path.Combine(staging, name);
                    var to = Path.Combine(root, name);
                    if (File.Exists(from)) File.Copy(from, to, true);
                    else if (File.Exists(to)) File.Delete(to);
                }
            }
            return new LibraryCheckResult(games.Count, Directory.GetFiles(Path.Combine(staging, "Packages")).Length, 0, 0, 0);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch (IOException) { }
        }
    }
}

public sealed record LibraryCheckResult(int Games, int Packages, int MissingPackages, int Recovered, int UnregisteredPackages);
