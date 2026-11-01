using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private async Task<ModConfiguration?> GetModAsync(GameInfo game)
    {
        if (server is null) return null;
        using var response = await http.GetAsync(server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/mod");
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength == 0) return null;
        return await response.Content.ReadFromJsonAsync<ModConfiguration>();
    }

    private string? FindBaseGameFolder(ModConfiguration config)
    {
        if (!settings.KnownGames.TryGetValue(config.BaseGameId, out var baseGame)) return null;
        if (settings.InstalledExecutables.TryGetValue(config.BaseGameId, out var exe) && File.Exists(exe))
            return Path.GetDirectoryName(exe);
        var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, baseGame.Name);
        return Directory.Exists(folder) && GameExecutableFinder.FindBest(folder, baseGame.Name) is not null
            ? folder : null;
    }

    private static string? ResolveWithin(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path : null;
    }

    private async void EditMod(GameInfo game)
    {
        if (server is null || string.IsNullOrWhiteSpace(AdminKeyBox.Password))
        {
            MessageBox.Show(this, "Zum Zuordnen eines Mods ist ein Admin-Schlüssel erforderlich.");
            return;
        }
        try
        {
            using var check = AdminRequest(HttpMethod.Get, "/api/admin/check");
            using var checkResponse = await http.SendAsync(check);
            if (!checkResponse.IsSuccessStatusCode)
            {
                MessageBox.Show(this, "Ungültiger Admin-Schlüssel.");
                return;
            }
            var games = await http.GetFromJsonAsync<GameInfo[]>(server + "/api/games") ?? [];
            var existing = await GetModAsync(game);
            var dialog = new ModSettingsWindow(games.Where(g => g.Id != game.Id).ToArray(), existing)
            { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Configuration is null) return;
            using var request = AdminRequest(HttpMethod.Put,
                "/api/admin/games/" + Uri.EscapeDataString(game.Id) + "/mod");
            request.Content = JsonContent.Create(dialog.Configuration);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            StatusText.Text = "Mod-Konfiguration gespeichert: " + game.Name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Mod-Konfiguration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> InstallModArchiveAsync(GameInfo game, ModConfiguration config, string archivePath)
    {
        var baseFolder = FindBaseGameFolder(config);
        if (baseFolder is null)
        {
            MessageBox.Show(this, "Das Hauptspiel ist nicht installiert. Bitte zuerst Company of Heroes installieren.",
                "Hauptspiel fehlt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (config.InstallerSteps.Length == 0)
        {
            MessageBox.Show(this, "Für diesen Mod wurden noch keine Installationsschritte hinterlegt.",
                "Mod-Installation", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var staging = Path.Combine(settings.DownloadDirectory, "LANLauncher-Mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await Task.Run(() =>
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
                long size = 0;
                foreach (var entry in archive.Entries)
                {
                    checked { size += entry.Length; }
                    if (size > 40L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("Mod-Archiv überschreitet 40 GB.");
                }
                archive.ExtractToDirectory(staging);
            });
            foreach (var step in config.InstallerSteps)
            {
                var installer = ResolveWithin(staging, step);
                if (installer is null || !File.Exists(installer))
                    throw new FileNotFoundException("Installationsschritt nicht im Mod-Paket gefunden: " + step);
                if (MessageBox.Show(this,
                    "Nächsten Installationsschritt starten?\n\n" + step +
                    "\n\nInstallationsziel im Assistenten auf den Hauptspielordner setzen:\n" +
                    baseFolder + "\n\nNach Abschluss des Assistenten mit dem nächsten Schritt fortfahren.",
                    "Mod installieren", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return false;
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installer)!
                }) ?? throw new IOException("Installer konnte nicht gestartet werden: " + step);
                await process.WaitForExitAsync();
                if (MessageBox.Show(this,
                    "Ist der Installationsschritt vollständig abgeschlossen?\n" + step +
                    "\n\nNur mit Ja wird der nächste Installer gestartet.",
                    "Installationsschritt prüfen", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            }
            var launcher = ResolveWithin(baseFolder, config.LauncherExe);
            if (launcher is null || !File.Exists(launcher))
            {
                MessageBox.Show(this, "Installationsschritte abgeschlossen, aber die Mod-EXE fehlt:\n" +
                    config.LauncherExe + "\n\nBitte die Installation prüfen.",
                    "Mod-EXE fehlt", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            settings.InstalledExecutables[game.Id] = launcher;
            settings.Save();
            StatusText.Text = "Mod installiert: " + game.Name;
            return true;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
