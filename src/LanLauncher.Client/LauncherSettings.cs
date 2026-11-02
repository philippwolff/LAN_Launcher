using System.IO;
using System.Text.Json;

namespace LanLauncher.Client;

public sealed class LauncherSettings
{
    public Dictionary<string, string> DownloadedPackages { get; set; } = new();
    public Dictionary<string, string> InstallerTypes { get; set; } = new();
    public Dictionary<string, LanLauncher.Shared.GameInfo> KnownGames { get; set; } = new();
    public string? LastOpenedGameId { get; set; }
    public Dictionary<string, string> InstalledExecutables { get; set; } = new();
    public Dictionary<string, List<GameLaunchOption>> AdditionalExecutables { get; set; } = new();
    public bool DarkMode { get; set; }
    public string InstallDirectory { get; set; } = @"C:\LAN_Launcher_Server\Games";
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LANLauncher", "settings.json");

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsFile)) ?? new();
        }
        catch (Exception) { /* Invalid/missing settings fall back to defaults. */ }
        return new LauncherSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var temporary = SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsFile, true);
    }
}

public sealed record GameLaunchOption(string Name, string ExecutablePath);
