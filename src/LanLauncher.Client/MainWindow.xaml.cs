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
                var sortedGames = games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
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
        switch (popup.SelectedAction)
        {
            case GameActionsWindow.GameAction.Start:
                StartGame(game);
                break;
            case GameActionsWindow.GameAction.Install:
                Install_Click(sender, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Download:
                Download_Click(sender, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Delete:
                DeleteGame_Click(sender, new RoutedEventArgs());
                break;
        }
    }

    private void StartGame(GameInfo game)
    {
        if (!settings.InstalledExecutables.TryGetValue(game.Id, out var executable) || !File.Exists(executable))
        {
            var selection = MessageBox.Show(this,
                "Für dieses Spiel wurde noch keine ausführbare Datei hinterlegt.\n\n" +
                "Möchtest du die bereits installierte Spiel-EXE jetzt auswählen?",
                "Spielstart einrichten", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (selection != MessageBoxResult.Yes) return;
            var picker = new OpenFileDialog { Filter = "Anwendungen (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            executable = picker.FileName;
            settings.InstalledExecutables[game.Id] = executable;
            settings.Save();
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
        if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
        {
            MessageBox.Show(this, "Der Zielordner ist nicht leer. Bitte wähle einen anderen Installationsordner.",
                "Installationsordner belegt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
                    var executables = Directory.GetFiles(targetFolder, "*.exe", SearchOption.AllDirectories)
                        .Where(path => !Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase)
                                    && !Path.GetFileName(path).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    string? executable = null;
                    if (executables.Length == 1)
                    {
                        executable = executables[0];
                    }
                    else if (executables.Length > 1)
                    {
                        MessageBox.Show(this,
                            "Das Spiel wurde entpackt. Es wurden mehrere EXE-Dateien gefunden.\n" +
                            "Bitte wähle die richtige Spiel- oder Setup-EXE aus.",
                            "EXE auswählen", MessageBoxButton.OK, MessageBoxImage.Information);
                        var executableDialog = new OpenFileDialog
                        {
                            Title = "Spiel-EXE auswählen",
                            Filter = "Anwendungen (*.exe)|*.exe",
                            InitialDirectory = targetFolder,
                            CheckFileExists = true
                        };
                        if (executableDialog.ShowDialog(this) == true &&
                            Path.GetFullPath(executableDialog.FileName).StartsWith(
                                Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
                var answer = MessageBox.Show(this,
                    "Die Installationsdatei wurde heruntergeladen.\n\n" +
                    "Möchtest du jetzt den EXE-Installationsassistenten starten?\n" +
                    "Führe nur Installationsdateien aus vertrauenswürdigen Quellen aus.",
                    "EXE-Installation starten", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes)
                {
                    // Der Installer benötigt seine Datei bis zum Ende; deshalb in den gewählten Ordner verschieben.
                    Directory.CreateDirectory(targetFolder);
                    var installerPath = Path.Combine(targetFolder, Path.GetFileName(fileName));
                    File.Move(tempPackage, installerPath, overwrite: true);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = targetFolder
                    });
                    StatusText.Text = "Installationsassistent gestartet: " + installerPath;
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
