using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Net.Http.Headers;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using LanLauncher.Shared;

namespace LanLauncher.Client;
public partial class MainWindow : Window
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly string clientId = Guid.NewGuid().ToString("N");
    private string? server;
    private LauncherSettings settings = LauncherSettings.Load();
    private bool updatingGames;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            settings = window.Settings;
            StatusText.Text = "Standardordner gespeichert.";
        }
    }
    private readonly System.Collections.Generic.HashSet<TransferProgressWindow> activeTransfers = new();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (activeTransfers.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                "Es laufen noch " + activeTransfers.Count + " Upload(s) oder Download(s).\n\n" +
                "Wenn du den Launcher schließt, werden diese Übertragungen abgebrochen.\n\n" +
                "Möchtest du den Launcher wirklich schließen?",
                "Laufende Dateiübertragungen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        timer.Stop();
        foreach (var transfer in activeTransfers.ToArray()) transfer.CancelTransfer();
        base.OnClosing(e);
    }
    public MainWindow()
    {
        InitializeComponent();
        timer.Tick += async (_, _) => await RefreshAsync();
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var address = ServerBox.Text.Trim();
            if (!address.StartsWith("http://") && !address.StartsWith("https://")) address = "http://" + address;
            server = address.TrimEnd('/');
            var name = NameBox.Text.Trim();
            if (name.Length is < 2 or > 32) { StatusText.Text = "Benutzername: 2–32 Zeichen."; return; }
            var response = await http.PostAsJsonAsync(server + "/api/join", new JoinRequest(name, clientId));
            if (!response.IsSuccessStatusCode) { StatusText.Text = "Anmeldung fehlgeschlagen: " + await response.Content.ReadAsStringAsync(); timer.Stop(); return; }
            StatusText.Text = "Verbunden als " + name;
            timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex) { timer.Stop(); StatusText.Text = "Verbindungsfehler: " + ex.Message; }
    }
    private async Task RefreshAsync()
    {
        if (server is null) return;
        try
        {
            await http.PostAsJsonAsync(server + "/api/status", new StatusRequest(clientId, "Online", null));
            using var gamesRequest = new HttpRequestMessage(HttpMethod.Get, server + "/api/games");
            gamesRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var gamesResponse = await http.SendAsync(gamesRequest);
            gamesResponse.EnsureSuccessStatusCode();
            var games = await gamesResponse.Content.ReadFromJsonAsync<GameInfo[]>() ?? [];
            var players = await http.GetFromJsonAsync<PlayerInfo[]>(server + "/api/players") ?? [];
            var selectedGameId = (GamesList.SelectedItem as GameInfo)?.Id;
            updatingGames = true;
            try
            {
                var sortedGames = LocalGameLibrary.Merge(games, settings);
                GamesList.ItemsSource = sortedGames;
                GamesList.SelectedItem = sortedGames.FirstOrDefault(g => g.Id == selectedGameId)
                    ?? sortedGames.FirstOrDefault(g => g.Id == settings.LastOpenedGameId)
                    ?? sortedGames.FirstOrDefault();
            }
            finally { updatingGames = false; }
            PlayersList.ItemsSource = players.Select(p => p.Name + " — " + p.Status + (p.GameId is null ? "" : " (" + p.GameId + ")")).ToArray();
            StatusText.Text = "Verbunden — " + players.Length + " Spieler online";
        }
        catch (Exception ex) { StatusText.Text = "Server nicht erreichbar: " + ex.Message; }
    }
    private void GamesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (updatingGames || GamesList.SelectedItem is not GameInfo game) return;
        OpenGameActions(game);
    }

    private void GamesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source &&
            System.Windows.Controls.ItemsControl.ContainerFromElement(GamesList, source)
                is System.Windows.Controls.ListBoxItem &&
            GamesList.SelectedItem is GameInfo game)
        {
            OpenGameActions(game);
            e.Handled = true;
        }
    }

    private void OpenGameActions(GameInfo game)
    {
        settings.LastOpenedGameId = game.Id;
        try { settings.Save(); }
        catch (Exception ex) { StatusText.Text = "Spielauswahl konnte nicht gespeichert werden: " + ex.Message; }
        var popup = new GameActionsWindow(game) { Owner = this };
        if (popup.ShowDialog() != true) return;
        if (GameDisplayNameConverter.LocalOnlyIds.Contains(game.Id) &&
            (popup.SelectedAction == GameActionsWindow.GameAction.Install ||
             popup.SelectedAction == GameActionsWindow.GameAction.Download ||
             popup.SelectedAction == GameActionsWindow.GameAction.Delete))
        {
            MessageBox.Show(this, "Das Spiel ist nur lokal verfügbar und nicht mehr auf dem Server.",
                "Nur lokal verfügbar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        switch (popup.SelectedAction)
        {
            case GameActionsWindow.GameAction.Start:
                StartGame(game);
                break;
            case GameActionsWindow.GameAction.Install:
                Install_Click(GamesList, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Download:
                Download_Click(GamesList, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Delete:
                DeleteGame_Click(GamesList, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Tags:
                EditTags(game);
                break;
            case GameActionsWindow.GameAction.Settings:
                OpenGameSettings(game);
                break;
            case GameActionsWindow.GameAction.Uninstall:
                UninstallGame(game);
                break;
        }
    }

    private void UninstallGame(GameInfo game)
    {
        var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        settings.InstalledExecutables.TryGetValue(game.Id, out var executable);
        var existingExe = !string.IsNullOrWhiteSpace(executable) && File.Exists(executable)
            ? executable : null;
        var root = Directory.Exists(folder) ? folder
            : existingExe is not null ? Path.GetDirectoryName(existingExe)! : folder;

        // Prefer the vendor's own uninstaller for EXE-based installations.
        var uninstallers = new List<string>();
        if (Directory.Exists(root))
        {
            try
            {
                uninstallers.AddRange(Directory.EnumerateFiles(root, "*.exe",
                    SearchOption.TopDirectoryOnly).Where(file =>
                    {
                        var name = Path.GetFileName(file);
                        return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase);
                    }));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        if (uninstallers.Count > 0)
        {
            var uninstaller = uninstallers.OrderBy(x => x).First();
            if (MessageBox.Show(this,
                "Das Deinstallationsprogramm des Spiels starten?\n\n" + uninstaller +
                "\n\nDer Deinstallationsassistent entscheidet, welche Dateien entfernt werden.",
                "Spiel deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uninstaller)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(uninstaller)!
                });
                StatusText.Text = "Deinstallationsassistent gestartet: " + game.Name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Deinstallationsprogramm konnte nicht gestartet werden:\n" + ex.Message,
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        // Never delete a game installed outside the launcher's dedicated folder.
        var expected = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        var installRoot = Path.GetFullPath(settings.InstallDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(folder) ||
            !expected.StartsWith(installRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "Kein vom Launcher verwalteter Installationsordner gefunden.\n\n" +
                "Bitte das Spiel über Windows Einstellungen > Apps > Installierte Apps deinstallieren.\n" +
                "Es wurden keine Dateien gelöscht.",
                "Spiel deinstallieren", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (settings.InstallerTypes.ContainsKey(game.Id))
        {
            MessageBox.Show(this,
                "Für dieses Spiel wurde ein EXE-Installer verwendet, aber kein Deinstallationsprogramm gefunden.\n\n" +
                "Bitte unter Windows Einstellungen > Apps > Installierte Apps deinstallieren.\n" +
                "Der Launcher löscht hier keine Dateien, um die Installation nicht zu beschädigen.",
                "Deinstallationsprogramm fehlt", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this,
            "Den lokal installierten Spielordner vollständig löschen?\n\n" + folder +
            "\n\nDies kann auch Spielstände und eigene Dateien im Ordner löschen. " +
            "Das Spielpaket auf dem Server bleibt erhalten.",
            "Spiel deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            Directory.Delete(folder, recursive: true);
            settings.InstalledExecutables.Remove(game.Id);
            settings.Save();
            StatusText.Text = "Spiel lokal deinstalliert: " + game.Name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Spielordner konnte nicht gelöscht werden:\n" + ex.Message,
                "Deinstallation fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenGameSettings(GameInfo game)
    {
        var window = new GameSettingsWindow(game, settings) { Owner = this };
        if (window.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(window.ExecutablePath))
            settings.InstalledExecutables.Remove(game.Id);
        else
            settings.InstalledExecutables[game.Id] = window.ExecutablePath;
        try
        {
            settings.Save();
            await UploadLaunchPathAsync(game, window.ExecutablePath);
            StatusText.Text = "Spieleinstellungen gespeichert: " + game.Name;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Spieleinstellungen konnten nicht gespeichert werden: " + ex.Message;
        }
    }

    private async void StartGame(GameInfo game)
    {
        if (!settings.InstalledExecutables.TryGetValue(game.Id, out var executable) || !File.Exists(executable))
        {
            var installFolder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
            var detected = await GetServerLaunchExecutableAsync(game, installFolder)
                ?? GameExecutableFinder.FindBest(installFolder, game.Name);
            if (detected is not null)
            {
                var confirmation = MessageBox.Show(this,
                    "Mögliche Spiel-EXE automatisch gefunden:\n" + detected +
                    "\n\nSoll diese Datei zum Starten verwendet werden?",
                    "Spiel-EXE erkannt", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirmation == MessageBoxResult.Yes)
                {
                    executable = detected;
                    settings.InstalledExecutables[game.Id] = executable;
                    settings.Save();
                }
            }
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                var selection = MessageBox.Show(this,
                    "Keine eindeutige Spiel-EXE gefunden.\n\n" +
                    "Möchtest du die ausführbare Datei manuell auswählen?\n" +
                    "Der Dateidialog öffnet sich im Installationsordner:\n" + installFolder,
                    "Spielstart einrichten", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (selection != MessageBoxResult.Yes) return;
                var picker = new OpenFileDialog
                {
                    Title = "Spiel-EXE auswählen",
                    Filter = "Anwendungen (*.exe)|*.exe",
                    CheckFileExists = true,
                    InitialDirectory = Directory.Exists(installFolder) ? installFolder : settings.InstallDirectory
                };
                if (picker.ShowDialog(this) != true) return;
                executable = picker.FileName;
                settings.InstalledExecutables[game.Id] = executable;
                settings.Save();
            }
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            });
            StatusText.Text = "Spiel gestartet: " + game.Name;
        }
        catch (Exception ex) { StatusText.Text = "Spielstart fehlgeschlagen: " + ex.Message; }
    }

    private sealed record ServerLaunchPath(string Path);

    private async Task<string?> GetServerLaunchExecutableAsync(GameInfo game, string folder)
    {
        if (server is null) return null;
        try
        {
            var info = await http.GetFromJsonAsync<ServerLaunchPath>(
                server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/launch-path");
            if (info is null || string.IsNullOrWhiteSpace(info.Path) || Path.IsPathRooted(info.Path))
                return null;
            var fullFolder = Path.GetFullPath(folder);
            var fullPath = Path.GetFullPath(Path.Combine(fullFolder, info.Path));
            if (!fullPath.StartsWith(fullFolder + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (HttpRequestException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private async Task UploadLaunchPathAsync(GameInfo game, string executable)
    {
        if (server is null || string.IsNullOrWhiteSpace(executable) ||
            string.IsNullOrWhiteSpace(AdminKeyBox.Password)) return;
        var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        var relative = Path.GetRelativePath(folder, executable);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar)) return;
        using var request = AdminRequest(HttpMethod.Put,
            "/api/admin/games/" + Uri.EscapeDataString(game.Id) + "/launch-path");
        request.Content = JsonContent.Create(new ServerLaunchPath(relative));
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            StatusText.Text = "Lokaler EXE-Pfad gespeichert; Server-Speicherung nicht möglich: " + response.StatusCode;
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, server + endpoint);
        request.Headers.Add("X-Admin-Key", AdminKeyBox.Password);
        return request;
    }
    private async void Inbox_Click(object sender, RoutedEventArgs e)
    {
        if (server is null) { StatusText.Text = "Zuerst verbinden."; return; }
        try
        {
            using var request = AdminRequest(HttpMethod.Get, "/api/admin/inbox");
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { StatusText.Text = "Admin-Zugriff verweigert: " + response.StatusCode; return; }
            InboxFiles.ItemsSource = await response.Content.ReadFromJsonAsync<string[]>() ?? [];
            StatusText.Text = "Server-Inbox aktualisiert.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || InboxFiles.SelectedItem is not string file) { StatusText.Text = "Verbinden und Datei aus der Inbox wählen."; return; }
        try
        {
            using var request = AdminRequest(HttpMethod.Post, "/api/admin/import");
            request.Content = JsonContent.Create(new { fileName = file, name = GameNameBox.Text, version = GameVersionBox.Text, description = "" });
            using var response = await http.SendAsync(request);
            StatusText.Text = response.IsSuccessStatusCode ? "Spiel importiert." : "Import fehlgeschlagen: " + await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) await RefreshAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (server is null) { StatusText.Text = "Zuerst verbinden."; return; }
        try
        {
            using var checkRequest = AdminRequest(HttpMethod.Get, "/api/admin/check");
            using var checkResponse = await http.SendAsync(checkRequest);
            if (!checkResponse.IsSuccessStatusCode)
            {
                StatusText.Text = "Upload abgelehnt: Admin-Schlüssel fehlt oder ist ungültig (HTTP " + (int)checkResponse.StatusCode + ").";
                return;
            }
            if (string.IsNullOrWhiteSpace(GameNameBox.Text) || string.IsNullOrWhiteSpace(GameVersionBox.Text))
            {
                StatusText.Text = "Bitte Spielname und Version eingeben.";
                return;
            }
        }
        catch (Exception ex) { StatusText.Text = "Admin-Prüfung fehlgeschlagen: " + ex.Message; return; }
        var dialog = new OpenFileDialog { Filter = "Spielepakete (*.zip;*.exe)|*.zip;*.exe" };
        if (dialog.ShowDialog() != true) return;
        var popup = new TransferProgressWindow("Upload: " + Path.GetFileName(dialog.FileName)) { Owner = this };
        activeTransfers.Add(popup);
        popup.Show();
        try
        {
            using var request = AdminRequest(HttpMethod.Post, "/api/admin/upload");
            using var multipart = new MultipartFormDataContent();
            using var file = File.OpenRead(dialog.FileName);
            using var stream = new ProgressStreamContent(file,
                (sent, total) => Dispatcher.InvokeAsync(() => popup.Update(sent, total)), popup.Cancellation);
            stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(stream, "file", Path.GetFileName(dialog.FileName));
            multipart.Add(new StringContent(GameNameBox.Text), "name");
            multipart.Add(new StringContent(GameVersionBox.Text), "version");
            request.Content = multipart;
            using var uploadClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            StatusText.Text = "Upload läuft …";
            using var response = await uploadClient.SendAsync(request, popup.Cancellation);
            popup.Finishing();
            StatusText.Text = response.IsSuccessStatusCode ? "Spiel hochgeladen." : "Upload fehlgeschlagen (HTTP " + (int)response.StatusCode + "): " + await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) await RefreshAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Upload abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Upload fehlgeschlagen: " + ex.Message; }
        finally { activeTransfers.Remove(popup); popup.CloseAfterTransfer(); }
    }

    private async void DeleteGame_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || GamesList.SelectedItem is not GameInfo game)
        {
            StatusText.Text = "Bitte zuerst verbinden und ein Spiel auswählen.";
            return;
        }
        var confirmation = MessageBox.Show(
            "Soll das Spiel \"" + game.Name + "\" (Version " + game.Version + ") wirklich vom Server gelöscht werden?\n\n" +
            "Der Eintrag in der Bibliothek und das gespeicherte ZIP-/EXE-Paket werden dauerhaft entfernt.\n" +
            "Bereits heruntergeladene Dateien auf anderen PCs bleiben erhalten.",
            "Spiel endgültig löschen?", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;
        try
        {
            using var request = AdminRequest(HttpMethod.Delete, "/api/admin/games/" + Uri.EscapeDataString(game.Id));
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                await RefreshAsync();
                StatusText.Text = "Spiel \"" + game.Name + "\" wurde vom Server gelöscht.";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                StatusText.Text = "Löschen verweigert: Admin-Schlüssel fehlt oder ist ungültig.";
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await RefreshAsync();
                StatusText.Text = "Spiel wurde auf dem Server nicht mehr gefunden.";
            }
            else
                StatusText.Text = "Löschen fehlgeschlagen (HTTP " + (int)response.StatusCode + "): " + await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { StatusText.Text = "Löschen fehlgeschlagen: " + ex.Message; }
    }


    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || GamesList.SelectedItem is not GameInfo game)
        {
            StatusText.Text = "Bitte ein Spiel auswählen.";
            return;
        }

        // Use the configured installation directory without asking every time.
        var targetFolder = Path.Combine(settings.InstallDirectory,
            string.Concat(game.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)));
        var popup = new TransferProgressWindow("Herunterladen und installieren: " + game.Name) { Owner = this };
        activeTransfers.Add(popup);
        popup.Show();
        Directory.CreateDirectory(settings.DownloadDirectory);
        var tempPackage = Path.Combine(settings.DownloadDirectory, "LANLauncher-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await downloadClient.GetAsync(
                server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/package",
                HttpCompletionOption.ResponseHeadersRead, popup.Cancellation);
            response.EnsureSuccessStatusCode();

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? "";
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension is not (".zip" or ".exe"))
                throw new InvalidDataException("Der Server hat keinen gültigen ZIP-/EXE-Dateityp übermittelt.");

            await using (var input = await response.Content.ReadAsStreamAsync(popup.Cancellation))
            await using (var output = new FileStream(tempPackage, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, useAsync: true))
            {
                var total = response.Content.Headers.ContentLength;
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, popup.Cancellation)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), popup.Cancellation);
                    received += count;
                    popup.Update(received, total);
                }
                await output.FlushAsync(popup.Cancellation);
                if (total.HasValue && received != total.Value)
                    throw new IOException("Der Download ist unvollständig.");
            }
            popup.Cancellation.ThrowIfCancellationRequested();
            popup.Finishing();
            activeTransfers.Remove(popup);
            popup.CloseAfterTransfer();

            if (extension == ".zip")
            {
                if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
                    throw new IOException("Der Zielordner ist nicht leer: " + targetFolder);
                // Extraktion in einen frischen Ordner. Keine Dateien anderer Installationen überschreiben.
                Directory.CreateDirectory(targetFolder);
                try
                {
                    await Task.Run(() =>
                    {
                        using var archive = System.IO.Compression.ZipFile.OpenRead(tempPackage);
                        const long maxUncompressedBytes = 40L * 1024 * 1024 * 1024;
                        long totalSize = 0;
                        foreach (var entry in archive.Entries)
                        {
                            checked { totalSize += entry.Length; }
                            if (totalSize > maxUncompressedBytes)
                                throw new InvalidDataException("Archiv überschreitet die maximale entpackte Größe (40 GB).");
                        }
                        archive.ExtractToDirectory(targetFolder, overwriteFiles: false);
                    });
                    StatusText.Text = "Spiel entpackt nach: " + targetFolder;
                    var executable = GameExecutableFinder.FindBest(targetFolder, game.Name);
                    if (executable is null)
                    {
                        MessageBox.Show(this,
                            "Keine eindeutige Spiel-EXE erkannt. Bitte wähle die richtige Datei aus.",
                            "Spiel-EXE auswählen", MessageBoxButton.OK, MessageBoxImage.Information);
                        var executableDialog = new OpenFileDialog
                        {
                            Title = "Spiel-EXE auswählen",
                            Filter = "Anwendungen (*.exe)|*.exe",
                            InitialDirectory = targetFolder,
                            CheckFileExists = true
                        };
                        if (executableDialog.ShowDialog(this) == true)
                            executable = executableDialog.FileName;
                    }

                    if (executable is not null)
                    {
                        settings.InstalledExecutables[game.Id] = executable;
                        settings.Save();
                        var start = MessageBox.Show(this,
                            "Das Spiel wurde entpackt nach:\n" + targetFolder +
                            "\n\nMöchtest du diese EXE jetzt ausführen?\n" +
                            executable +
                            "\n\nStarte nur Programme aus vertrauenswürdigen ZIP-Dateien.",
                            "EXE nach Installation starten", MessageBoxButton.YesNo,
                            MessageBoxImage.Warning, MessageBoxResult.No);
                        if (start == MessageBoxResult.Yes)
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                            {
                                UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(executable)!
                            });
                            StatusText.Text = "EXE gestartet: " + executable;
                        }
                    }
                    else
                    {
                        MessageBox.Show(this,
                            "Das Spiel wurde entpackt nach:\n" + targetFolder +
                            "\n\nEs wurde keine eindeutige Spiel-EXE zum Starten ausgewählt.",
                            "Installation abgeschlossen", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch
                {
                    try { Directory.Delete(targetFolder, recursive: true); } catch (IOException) { }
                    throw;
                }
            }
            else
            {
                var detectedType = InstallerDetector.Detect(tempPackage, game.Name);
                var savedType = detectedType != "manual"
                    ? detectedType
                    : settings.InstallerTypes.GetValueOrDefault(game.Id) ?? "manual";
                var options = new InstallerOptionsWindow(game.Name, targetFolder, savedType, detectedType != "manual") { Owner = this };
                if (options.ShowDialog() == true)
                {
                    // Save the installer outside the actual game destination; the installer
                    // itself controls where game files are installed.
                    var installerFileName = Path.GetFileName(fileName);
                    if (string.IsNullOrWhiteSpace(installerFileName))
                        installerFileName = "setup.exe";
                    var installerPath = Path.Combine(settings.DownloadDirectory,
                        "LANLauncher-" + Guid.NewGuid().ToString("N") + "-" + installerFileName);
                    File.Move(tempPackage, installerPath);
                    var info = new System.Diagnostics.ProcessStartInfo(installerPath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = settings.DownloadDirectory
                    };
                    if (options.InstallerType == "nsis")
                    {
                        // NSIS requires /D= as the final argument, without quotes.
                        info.Arguments = "/D=" + targetFolder;
                    }
                    else if (options.InstallerType == "inno")
                    {
                        info.ArgumentList.Add("/DIR=" + targetFolder);
                    }
                    System.Diagnostics.Process.Start(info);
                    settings.InstallerTypes[game.Id] = options.InstallerType;
                    settings.Save();
                    StatusText.Text = "Installationsassistent gestartet. Zielordner im Assistenten prüfen: " + targetFolder;
                }
                else
                    StatusText.Text = "EXE-Installation nicht gestartet.";
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "Installation/Download abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Installation fehlgeschlagen: " + ex.Message; }
        finally
        {
            if (File.Exists(tempPackage))
                try { File.Delete(tempPackage); } catch (IOException) { }
            if (activeTransfers.Remove(popup)) popup.CloseAfterTransfer();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (server is null || GamesList.SelectedItem is not GameInfo game) { StatusText.Text = "Bitte ein Spiel auswählen."; return; }
        var popup = new TransferProgressWindow("Download: " + game.Name) { Owner = this };
        activeTransfers.Add(popup);
        popup.Show();
        string? outputPath = null;
        string? temporaryPath = null;
        bool completed = false;
        try
        {
            using var downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await downloadClient.GetAsync(server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/package",
                HttpCompletionOption.ResponseHeadersRead, popup.Cancellation);
            response.EnsureSuccessStatusCode();
            var suggested = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? (game.Name + ".zip");
            Directory.CreateDirectory(settings.DownloadDirectory);
            var dialog = new SaveFileDialog { FileName = suggested, InitialDirectory = settings.DownloadDirectory, Filter = "Alle Dateien|*.*" };
            if (dialog.ShowDialog() != true) { StatusText.Text = "Download abgebrochen."; return; }
            outputPath = dialog.FileName;
            temporaryPath = outputPath + ".part";
            await using var input = await response.Content.ReadAsStreamAsync(popup.Cancellation);
            await using var output = File.Create(temporaryPath);
            var total = response.Content.Headers.ContentLength;
            var buffer = new byte[128 * 1024];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, popup.Cancellation)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), popup.Cancellation);
                received += count;
                popup.Update(received, total);
            }
            await output.FlushAsync(popup.Cancellation);
            popup.Cancellation.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, true);
            completed = true;
            StatusText.Text = "Paket gespeichert: " + outputPath;
        }
        catch (OperationCanceledException) { StatusText.Text = "Download abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Download fehlgeschlagen: " + ex.Message; }
        finally
        {
            if (!completed && temporaryPath is not null && File.Exists(temporaryPath))
                try { File.Delete(temporaryPath); } catch (IOException) { }
            activeTransfers.Remove(popup);
            popup.CloseAfterTransfer();
        }
    }

}
