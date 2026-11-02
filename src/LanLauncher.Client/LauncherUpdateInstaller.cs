using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace LanLauncher.Client;

internal static class LauncherUpdateInstaller
{
    internal static async Task InstallAsync(LauncherRelease release, int launcherProcessId)
    {
        var root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var work = Path.Combine(Path.GetTempPath(), "LANLauncher-Update-" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(work, "client.zip");
        var stage = Path.Combine(work, "staged");
        Directory.CreateDirectory(stage);
        try
        {
            using var client = LauncherUpdateChecker.CreateClient(TimeSpan.FromMinutes(15));
            using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = File.Create(zip);
                await source.CopyToAsync(destination);
            }
            ZipFile.ExtractToDirectory(zip, stage);
            if (!File.Exists(Path.Combine(stage, "LanLauncher.Client.exe")) ||
                !File.Exists(Path.Combine(stage, "launcher-version.txt")) ||
                File.ReadAllText(Path.Combine(stage, "launcher-version.txt")).Trim() != release.Tag)
                throw new InvalidDataException("Das heruntergeladene Release-Paket ist unvollständig oder hat die falsche Version.");

            // Run the updater outside the installation directory: running executables cannot be replaced.
            var script = Path.Combine(work, "apply-update.ps1");
            var scriptContent = """
param([int]$LauncherPid, [string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
try {
    $process = Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue
    if ($process) { $process.WaitForExit(120000) }
    if (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue) {
        throw 'Launcher ist noch geöffnet. Update abgebrochen.'
    }
    Copy-Item -LiteralPath (Join-Path $Source '*') -Destination $Destination -Recurse -Force
    Start-Process -FilePath (Join-Path $Destination 'LanLauncher.Client.exe') -WorkingDirectory $Destination
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'LAN Launcher Update fehlgeschlagen') | Out-Null
}
""";
            // Copy-Item with -LiteralPath does not expand wildcards; enumerate staged files instead.
            scriptContent = scriptContent.Replace(
                "Copy-Item -LiteralPath (Join-Path $Source '*') -Destination $Destination -Recurse -Force",
                "Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force");
            await File.WriteAllTextAsync(script, scriptContent);
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "RemoteSigned", "-File", script,
                    "-LauncherPid", launcherProcessId.ToString(), "-Source", stage, "-Destination", root }
            }) ?? throw new InvalidOperationException("Updater konnte nicht gestartet werden.");
        }
        catch
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
            throw;
        }
    }
}
