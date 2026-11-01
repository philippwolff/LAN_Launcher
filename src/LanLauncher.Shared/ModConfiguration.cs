namespace LanLauncher.Shared;

// A mod is an independent catalog entry whose files are installed into its base game's folder.
public sealed record ModConfiguration(string BaseGameId, string LauncherExe, string[] InstallerSteps);
