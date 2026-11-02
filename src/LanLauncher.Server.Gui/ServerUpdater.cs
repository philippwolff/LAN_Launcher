using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LanLauncher.Server.Gui;

internal sealed record ServerRelease(Version Version, string Tag, string DownloadUrl);

internal static class ServerUpdater
{
    private const string ApiUrl = "https://api.github.com/repos/philippwolff/LAN_Launcher/releases/latest";
    private const string AssetName = "LAN_Party_Server-win-x64.zip";

    private static HttpClient Client(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LANLauncherServer", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static async Task<(ServerRelease? Release, string Message)> CheckAsync()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "launcher-version.txt");
        if (!File.Exists(marker) || !Version.TryParse(File.ReadAllText(marker).Trim().TrimStart('v', 'V'), out var current))
            return (null, "Keine Release-Version installiert. Bitte zuerst ein veröffentlichtes Server-Paket installieren.");
        using var client = Client(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(ApiUrl);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return (null, "Die neueste Release-Version ist ungültig.");
        if (latest <= current)
            return (null, "Server ist aktuell (v" + current + ").");
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() == AssetName)
                return (new ServerRelease(latest, tag, asset.GetProperty("browser_download_url").GetString()!),
                    "Server-Update " + tag + " verfügbar (installiert: v" + current + ").");
        }
        return (null, "Das neueste Release enthält noch kein Server-Paket.");
    }

    internal static async Task InstallAsync(ServerRelease release, int guiPid, int? serverPid)
    {
        var destination = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var work = Path.Combine(Path.GetTempPath(), "LANLauncher-ServerUpdate-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(work, "staged");
        Directory.CreateDirectory(stage);
        try
        {
            using var client = Client(TimeSpan.FromMinutes(20));
            using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var zip = Path.Combine(work, "server.zip");
            await using (var output = File.Create(zip))
                await response.Content.CopyToAsync(output);
            ZipFile.ExtractToDirectory(zip, stage);
            if (!File.Exists(Path.Combine(stage, "LanLauncher.Server.Gui.exe")) ||
                !File.Exists(Path.Combine(stage, "Server", "LanLauncher.Server.exe")) ||
                !File.Exists(Path.Combine(stage, "launcher-version.txt")) ||
                File.ReadAllText(Path.Combine(stage, "launcher-version.txt")).Trim() != release.Tag)
                throw new InvalidDataException("Server-Update unvollständig oder falsche Version.");

            var script = Path.Combine(work, "apply-update.ps1");
            var scriptContent = """
param([int]$GuiPid, [int]$ServerPid, [string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
try {
    if ($ServerPid -gt 0) {
        $server = Get-Process -Id $ServerPid -ErrorAction SilentlyContinue
        if ($server) { Stop-Process -Id $ServerPid -Force -ErrorAction Stop }
        $server = Get-Process -Id $ServerPid -ErrorAction SilentlyContinue
        if ($server) { $server.WaitForExit(30000) }
    }
    $gui = Get-Process -Id $GuiPid -ErrorAction SilentlyContinue
    if ($gui) { $gui.WaitForExit(120000) }
    if (Get-Process -Id $GuiPid -ErrorAction SilentlyContinue) { throw 'Server-GUI noch geöffnet.' }
    if ($ServerPid -gt 0 -and (Get-Process -Id $ServerPid -ErrorAction SilentlyContinue)) { throw 'Serverprozess noch aktiv.' }
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        if ($_.Name -ne 'Storage') {
            Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
        }
    }
    Start-Process -FilePath (Join-Path $Destination 'LanLauncher.Server.Gui.exe') -WorkingDirectory $Destination
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'Server-Update fehlgeschlagen') | Out-Null
}
""";
            await File.WriteAllTextAsync(script, scriptContent);
            _ = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "RemoteSigned", "-File", script,
                    "-GuiPid", guiPid.ToString(), "-ServerPid", (serverPid ?? 0).ToString(),
                    "-Source", stage, "-Destination", destination }
            }) ?? throw new InvalidOperationException("Updater konnte nicht gestartet werden.");
        }
        catch
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
            throw;
        }
    }
}
