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
    private bool serverConnected;

    private LauncherRelease? availableUpdate;
    private bool checkingUpdates;

    private async Task CheckForUpdatesAsync()
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        try
        {
            var result = await LauncherUpdateChecker.CheckAsync();
            availableUpdate = result.Available == true ? result.Release : null;
            UpdateButton.Content = availableUpdate is not null ? "Update verfügbar!" : "Updates prüfen";
            UpdateButton.ToolTip = result.Details;
            UpdateButton.FontWeight = availableUpdate is not null ? FontWeights.Bold : FontWeights.Normal;
        }
        finally { checkingUpdates = false; }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            await CheckForUpdatesAsync();
            if (availableUpdate is null)
            {
                MessageBox.Show(this, UpdateButton.ToolTip?.ToString() ?? "Kein Update verfügbar.",
                    "Launcher-Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (activeTransfers.Count > 0)
            {
                MessageBox.Show(this, "Bitte zuerst alle laufenden Downloads und Uploads abschließen.",
                    "Update zurückgestellt", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(this,
                "Neue Version: " + availableUpdate.Tag +
                "\n\nDas Update wird heruntergeladen und anschließend der Launcher neu gestartet." +
                "\nLaufende Spiele bitte vorher speichern und schließen." +
                "\n\nJetzt installieren?",
                "Launcher-Update verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            StatusText.Text = "Launcher-Update wird heruntergeladen...";
            await LauncherUpdateInstaller.InstallAsync(availableUpdate, Environment.ProcessId);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Update konnte nicht installiert werden:\n" + ex.Message,
                "Update fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            settings = window.Settings;
            ThemeManager.Apply(settings.DarkMode);
            StatusText.Text = "Einstellungen gespeichert.";
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
        try
        {
            var versionFile = Path.Combine(AppContext.BaseDirectory, "launcher-version.txt");
            var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
            LauncherVersionText.Text = System.Text.RegularExpressions.Regex.IsMatch(version, @"^v[0-9]+\.[0-9]+\.[0-9]+$")
                ? version : "Entwicklungsversion";
        }
        catch (IOException) { LauncherVersionText.Text = "Version unbekannt"; }
        catch (UnauthorizedAccessException) { LauncherVersionText.Text = "Version unbekannt"; }
        timer.Tick += async (_, _) => await RefreshAsync();
        ShowOfflineLibrary("Offline – lokal installierte Spiele verfügbar");
        SelectFirstGameIfNeeded();
        Loaded += async (_, _) => { ThemeManager.Apply(settings.DarkMode); await CheckForUpdatesAsync(); };
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
            if (!response.IsSuccessStatusCode) { serverConnected = false; timer.Stop(); ShowOfflineLibrary("Anmeldung fehlgeschlagen – lokale Spiele verfügbar"); return; }
            serverConnected = true;
            StatusText.Text = "Verbunden als " + name;
            timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex) { serverConnected = false; timer.Stop(); ShowOfflineLibrary("Server nicht erreichbar – Offline-Modus: " + ex.Message); }
    }
    private async Task RefreshAsync()
    {
        if (server is null || !serverConnected) return;
        try
        {
            await UpdatePresenceAsync();
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
                InitializeTagFilters();
                allLibraryGames = LocalGameLibrary.Merge(games, settings);
                await LoadLibraryTagsAsync(games);
                ApplyLibraryTagFilters(selectedGameId ?? settings.LastOpenedGameId);
                SelectFirstGameIfNeeded();
            }
            finally { updatingGames = false; }
            var gameNames = games.Concat(settings.KnownGames.Values)
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            PlayersList.ItemsSource = players.Select(p =>
                p.Name + " — " + (p.Status == "Im Spiel" && p.GameId is not null
                    ? "Spielt: " + (gameNames.TryGetValue(p.GameId, out var title) ? title : p.GameId)
                    : "Online")).ToArray();
            StatusText.Text = "Verbunden — " + players.Length + " Spieler online";
        }
        catch (Exception ex) { serverConnected = false; ShowOfflineLibrary("Server nicht erreichbar – Offline-Modus: " + ex.Message); }
    }
    private bool IsGameInstalled(GameInfo game)
    {
        if (settings.InstalledExecutables.TryGetValue(game.Id, out var exe) &&
            !string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            return true;
        var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        if (GameExecutableFinder.FindBest(folder, game.Name) is not null) return true;
        return settings.AdditionalExecutables.TryGetValue(game.Id, out var extras) &&
            extras.Any(x => File.Exists(x.ExecutablePath));
    }

    private void UpdateSelectedGameActions()
    {
        var installed = GamesList.SelectedItem is GameInfo game && IsGameInstalled(game);
        ActionStartButton.IsEnabled = installed;
        ActionUninstallButton.IsEnabled = installed;
    }

    private void SelectFirstGameIfNeeded()
    {
        if (GamesList.SelectedItem is not GameInfo && GamesList.Items.Count > 0)
            GamesList.SelectedIndex = 0;
        if (GamesList.SelectedItem is GameInfo selected)
            SelectedGameTitle.Text = selected.Name + " (Version " + selected.Version + ")";
        else
            SelectedGameTitle.Text = "Kein Spiel ausgewählt";
        UpdateSelectedGameActions();
        _ = LoadSelectedGameStartNoticeAsync();
    }

    private void RunSelectedGameAction(GameActionsWindow.GameAction action)
    {
        if (GamesList.SelectedItem is GameInfo game)
        {
            if (action is GameActionsWindow.GameAction.Start or GameActionsWindow.GameAction.Uninstall && !IsGameInstalled(game)) return;
            OpenGameActions(game, action);
        }
    }
    private async void HideGame_Click(object sender, RoutedEventArgs e)
    {
        if (GamesList.SelectedItem is not GameInfo game) return;
        if (MessageBox.Show(this,
                "Den Bibliothekseintrag \"" + game.Name + "\" ausblenden?\n\n" +
                "Das Spiel und seine Dateien werden nicht gelöscht. Du kannst den Eintrag jederzeit wiederherstellen.",
                "Spiel ausblenden", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        settings.HiddenGameIds.Add(game.Id);
        settings.Save();
        if (serverConnected) await RefreshAsync();
        else ShowOfflineLibrary("Eintrag ausgeblendet");
        StatusText.Text = "Ausgeblendet: " + game.Name;
    }

    private async void RestoreHiddenGames_Click(object sender, RoutedEventArgs e)
    {
        if (settings.HiddenGameIds.Count == 0)
        {
            MessageBox.Show(this, "Es sind keine Spiele ausgeblendet.",
                "Ausgeblendete Spiele", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var names = settings.HiddenGameIds.Select(id =>
            settings.KnownGames.TryGetValue(id, out var game) ? game.Name : id);
        if (MessageBox.Show(this,
                "Folgende Einträge wieder einblenden?\n\n" +
                string.Join("\n", names) +
                "\n\nEs werden keine Dateien verändert.",
                "Ausgeblendete Spiele", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        settings.HiddenGameIds.Clear();
        settings.Save();
        if (serverConnected) await RefreshAsync();
        else ShowOfflineLibrary("Alle ausgeblendeten Einträge wiederhergestellt");
    }

    private void ActionStart_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Start);
    private void ActionInstall_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Install);
    private void ActionLocalInstall_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.LocalInstall);
    private void ActionDownload_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Download);
    private void ActionUninstall_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Uninstall);
    private void ActionSettings_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Settings);
    private void ActionDelete_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Delete);
    private void ActionMod_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Mod);
    private void ActionNotice_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Notice);
    private void ActionTags_Click(object sender, RoutedEventArgs e) => RunSelectedGameAction(GameActionsWindow.GameAction.Tags);

    private void GamesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (updatingGames || GamesList.SelectedItem is not GameInfo game) return;
        SelectedGameTitle.Text = game.Name + " (Version " + game.Version + ")";
        UpdateSelectedGameActions();
        _ = LoadSelectedGameStartNoticeAsync();
    }

    private sealed record GameNoticeDisplay(string? Text);

    private async Task LoadSelectedGameStartNoticeAsync()
    {
        var game = GamesList.SelectedItem as GameInfo;
        if (game is null)
        {
            SelectedGameStartNotice.Text = "Kein Spiel ausgewählt.";
            return;
        }
        if (!serverConnected || string.IsNullOrWhiteSpace(server))
        {
            SelectedGameStartNotice.Text = "Starthinweise sind ohne Serververbindung nicht verfügbar.";
            return;
        }
        var selectedId = game.Id;
        SelectedGameStartNotice.Text = "Starthinweise werden geladen...";
        try
        {
            var notice = await http.GetFromJsonAsync<GameNoticeDisplay>(
                server + "/api/games/" + Uri.EscapeDataString(selectedId) + "/start-notice");
            if ((GamesList.SelectedItem as GameInfo)?.Id != selectedId) return;
            SelectedGameStartNotice.Text = string.IsNullOrWhiteSpace(notice?.Text)
                ? "Für dieses Spiel sind keine Starthinweise hinterlegt."
                : notice.Text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if ((GamesList.SelectedItem as GameInfo)?.Id == selectedId)
                SelectedGameStartNotice.Text = "Starthinweise konnten nicht geladen werden.";
        }
    }

    private void GamesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.DependencyObject source &&
            System.Windows.Controls.ItemsControl.ContainerFromElement(GamesList, source)
                is System.Windows.Controls.ListBoxItem &&
            GamesList.SelectedItem is GameInfo game)
        {
            StartGame(game);
            e.Handled = true;
        }
    }

    private async void OpenGameActions(GameInfo game, GameActionsWindow.GameAction? directAction = null)
    {
        settings.LastOpenedGameId = game.Id;
        try { settings.Save(); }
        catch (Exception ex) { StatusText.Text = "Spielauswahl konnte nicht gespeichert werden: " + ex.Message; }
        var popup = new GameActionsWindow(game) { Owner = this };
        var hasInstalledExe = settings.InstalledExecutables.TryGetValue(game.Id, out var installedExe)
            && !string.IsNullOrWhiteSpace(installedExe) && File.Exists(installedExe);
        if (!hasInstalledExe)
        {
            var gameFolder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
            hasInstalledExe = GameExecutableFinder.FindBest(gameFolder, game.Name) is not null;
        }
        ModConfiguration? modDetails = null;
        if (serverConnected) try { modDetails = await GetModAsync(game); } catch (Exception) { }
        if (modDetails is not null)
        {
            var baseFolder = FindBaseGameFolder(modDetails);
            var modExe = baseFolder is null ? null : ResolveWithin(baseFolder, modDetails.LauncherExe);
            hasInstalledExe = modExe is not null && File.Exists(modExe);
            if (hasInstalledExe && !settings.InstalledExecutables.ContainsKey(game.Id))
            {
                settings.InstalledExecutables[game.Id] = modExe!;
                settings.Save();
            }
        }
        popup.SetInstalled(hasInstalledExe);
        popup.SetOffline(!serverConnected);
        popup.SetDownloaded(TryGetDownloadedPackage(game, out _));
        if (serverConnected && server is not null && !GameDisplayNameConverter.LocalOnlyIds.Contains(game.Id))
        {
            try
            {
                var tags = await http.GetFromJsonAsync<GameTags>(server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/tags");
                if (tags is not null) popup.ShowTags(tags);
            }
            catch (Exception) { /* Old server versions may not support tags yet. */ }
        }
        if (directAction is null && popup.ShowDialog() != true) return;
        var selectedAction = directAction ?? popup.SelectedAction;
        if (serverConnected && GameDisplayNameConverter.LocalOnlyIds.Contains(game.Id) &&
            (selectedAction == GameActionsWindow.GameAction.Install ||
             selectedAction == GameActionsWindow.GameAction.LocalInstall ||
             selectedAction == GameActionsWindow.GameAction.Download ||
             selectedAction == GameActionsWindow.GameAction.Delete))
        {
            MessageBox.Show(this, "Das Spiel ist nur lokal verfügbar und nicht mehr auf dem Server.",
                "Nur lokal verfügbar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        switch (selectedAction)
        {
            case GameActionsWindow.GameAction.Start:
                StartGame(game);
                break;
            case GameActionsWindow.GameAction.Install:
                Install_Click(GamesList, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.LocalInstall:
                InstallLocalPackage(game);
                break;
            case GameActionsWindow.GameAction.Download:
                Download_Click(GamesList, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Delete:
                DeleteGame_Click(GamesList, new RoutedEventArgs());
                break;
            case GameActionsWindow.GameAction.Notice:
                EditStartNotice(game);
                break;
            case GameActionsWindow.GameAction.Mod:
                EditMod(game);
                break;
            case GameActionsWindow.GameAction.Tags:
                EditTags(game);
                break;
            case GameActionsWindow.GameAction.Settings:
                OpenGameSettings(game);
                break;
            case GameActionsWindow.GameAction.Uninstall:
                if (serverConnected && await GetModAsync(game) is not null)
                    MessageBox.Show(this, "Der Mod teilt sich den Hauptspielordner. Eine automatische Deinstallation könnte Company of Heroes beschädigen. Bitte den offiziellen Mod-Deinstaller verwenden; der Launcher löscht keine gemeinsamen Dateien.", "Mod sicher deinstallieren", MessageBoxButton.OK, MessageBoxImage.Warning);
                else UninstallGame(game);
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
        if (window.AdditionalExecutables.Count == 0)
            settings.AdditionalExecutables.Remove(game.Id);
        else
            settings.AdditionalExecutables[game.Id] = window.AdditionalExecutables.ToList();
        foreach (var path in settings.CompatibilityModes.Keys.ToArray())
            if (window.CompatibilityModes.ContainsKey(path) || path == window.ExecutablePath ||
                window.AdditionalExecutables.Any(x => x.ExecutablePath == path))
                settings.CompatibilityModes.Remove(path);
        foreach (var entry in window.CompatibilityModes)
            settings.CompatibilityModes[entry.Key] = entry.Value;
        UpdateSelectedGameActions();
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
        var serverPaths = serverConnected ? await GetServerLaunchOptionsAsync(game) : new List<string>();
        var gameFolder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        var serverChoices = serverPaths.Select(path => new
        {
            Label = Path.GetFileNameWithoutExtension(path),
            FullPath = Path.GetFullPath(Path.Combine(gameFolder, path))
        }).Where(x => x.FullPath.StartsWith(Path.GetFullPath(gameFolder) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) && File.Exists(x.FullPath))
          .Select(x => new GameLaunchOption(x.Label, x.FullPath)).ToList();
        settings.InstalledExecutables.TryGetValue(game.Id, out var executable);
        if (serverChoices.Count == 0 && (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)))
        {
            var installFolder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
            var detected = (serverConnected ? await GetServerLaunchExecutableAsync(game, installFolder) : null)
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
        var launchOptions = new List<GameLaunchOption>();
        if (serverChoices.Count > 0)
            launchOptions.AddRange(serverChoices);
        else if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            launchOptions.Add(new GameLaunchOption("Hauptspiel", executable));
        if (settings.AdditionalExecutables.TryGetValue(game.Id, out var extras))
            launchOptions.AddRange(extras.Where(x => File.Exists(x.ExecutablePath)));
        if (launchOptions.Count == 0)
        {
            StatusText.Text = "Keine gültige Spiel-EXE vorhanden.";
            return;
        }
        if (launchOptions.Count > 1)
        {
            var picker = new GameLaunchPicker(game.Name, launchOptions) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedOption is null) return;
            executable = picker.SelectedOption.ExecutablePath;
        }
        else executable = launchOptions[0].ExecutablePath;
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = !settings.CompatibilityModes.ContainsKey(executable),
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            if (settings.CompatibilityModes.TryGetValue(executable, out var compatibilityMode) &&
                !string.IsNullOrWhiteSpace(compatibilityMode))
                startInfo.Environment["__COMPAT_LAYER"] = compatibilityMode;
            var process = System.Diagnostics.Process.Start(startInfo);
            TrackGameProcess(process, game.Id);
            StatusText.Text = "Spiel gestartet: " + game.Name;
            if (serverConnected) await ShowGameStartNoticeAsync(game);
        }
        catch (Exception ex) { StatusText.Text = "Spielstart fehlgeschlagen: " + ex.Message; }
    }

    private sealed record ServerLaunchPath(string Path);

    private async Task<List<string>> GetServerLaunchOptionsAsync(GameInfo game)
    {
        if (server is null) return [];
        try
        {
            var result = await http.GetFromJsonAsync<ServerLaunchOptions>(
                server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/launch-options");
            return result?.Paths?.Where(p => !string.IsNullOrWhiteSpace(p) &&
                !Path.IsPathRooted(p) &&
                !p.Split(new[] { '/', (char)92 }).Any(part => part is "." or ".." or "") &&
                p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Take(2).ToList() ?? [];
        }
        catch (HttpRequestException) { return []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private sealed record ServerLaunchOptions(string[] Paths);

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
            if (response.IsSuccessStatusCode)
            {
                var imported = await response.Content.ReadFromJsonAsync<GameInfo>();
                if (imported is not null) await SaveTagsAsync(imported.Id, pendingTags);
                pendingTags = new([], []);
                await RefreshAsync();
            }
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
                (sent, total) => Dispatcher.InvokeAsync(() =>
                    { if (total > 0 && sent >= total) popup.Finishing(); else popup.Update(sent, total); }), popup.Cancellation);
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
            if (response.IsSuccessStatusCode)
            {
                var uploaded = await response.Content.ReadFromJsonAsync<GameInfo>();
                if (uploaded is not null) await SaveTagsAsync(uploaded.Id, pendingTags);
                pendingTags = new([], []);
                await RefreshAsync();
            }
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


    private string? requestedLocalPackage;

    private void InstallLocalPackage(GameInfo game)
    {
        if (!TryGetDownloadedPackage(game, out var package))
        {
            MessageBox.Show(this, "Kein vollständig heruntergeladenes Paket gefunden.", "Installieren");
            return;
        }
        requestedLocalPackage = package;
        Install_Click(GamesList, new RoutedEventArgs());
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var localPackage = requestedLocalPackage;
        requestedLocalPackage = null;
        if (server is null || GamesList.SelectedItem is not GameInfo game)
        {
            StatusText.Text = "Bitte ein Spiel auswählen.";
            return;
        }

        ModConfiguration? modConfig;
        try { modConfig = serverConnected ? await GetModAsync(game) : null; }
        catch (Exception ex)
        {
            StatusText.Text = "Mod-Konfiguration nicht erreichbar: " + ex.Message;
            return;
        }
        if (modConfig is not null && FindBaseGameFolder(modConfig) is null)
        {
            MessageBox.Show(this, "Bitte zuerst das Hauptspiel installieren.",
                "Mod benötigt Hauptspiel", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            string fileName;
            string extension;
            if (localPackage is not null)
            {
                if (!File.Exists(localPackage) || Path.GetExtension(localPackage).ToLowerInvariant() is not (".zip" or ".exe"))
                    throw new FileNotFoundException("Das heruntergeladene ZIP-/EXE-Paket ist nicht mehr vorhanden.", localPackage);
                fileName = Path.GetFileName(localPackage);
                extension = Path.GetExtension(fileName).ToLowerInvariant();
                await Task.Run(() => File.Copy(localPackage, tempPackage, overwrite: false), popup.Cancellation);
            }
            else
            {
                using var downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var response = await downloadClient.GetAsync(
                    server + "/api/games/" + Uri.EscapeDataString(game.Id) + "/package",
                    HttpCompletionOption.ResponseHeadersRead, popup.Cancellation);
                response.EnsureSuccessStatusCode();
    
                fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                    ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                    ?? "";
                extension = Path.GetExtension(fileName).ToLowerInvariant();
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
    
            }
            popup.Cancellation.ThrowIfCancellationRequested();
            popup.Finishing();
            activeTransfers.Remove(popup);
            popup.CloseAfterTransfer();

            if (modConfig is not null)
            {
                if (extension != ".zip")
                    throw new InvalidDataException("Mehrstufige Mods müssen als ZIP mit allen Installern hochgeladen werden.");
                await InstallModArchiveAsync(game, modConfig, tempPackage);
            }
            else if (extension == ".zip")
            {
                if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
                    throw new IOException("Der Zielordner ist nicht leer: " + targetFolder);
                // Extract into a unique temporary directory, never into Games.
                var extractionFolder = Path.Combine(Path.GetTempPath(), "LANLauncher-Install-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractionFolder);
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
                        archive.ExtractToDirectory(extractionFolder, overwriteFiles: false);
                    });
                    StatusText.Text = "Installationspaket temporär entpackt.";
                    // Installer archives contain setup programs rather than an already playable game.
                    // Let the user explicitly approve each executable, in a predictable order.
                    var installers = Directory.GetFiles(extractionFolder, "*.exe", SearchOption.AllDirectories)
                        .Where(path =>
                        {
                            var name = Path.GetFileName(path);
                            return name.StartsWith("setup", StringComparison.OrdinalIgnoreCase) ||
                                   name.StartsWith("install", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("fix", StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderBy(path =>
                        {
                            var name = Path.GetFileName(path);
                            return name.StartsWith("setup", StringComparison.OrdinalIgnoreCase) ||
                                   name.StartsWith("install", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                        })
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (installers.Length > 0)
                    {
                        var confirmation = MessageBox.Show(this,
                            "Dieses Paket enthält " + installers.Length + " Installationsprogramme.\n\n" +
                            string.Join("\n", installers.Select((path, index) =>
                                (index + 1) + ". " + Path.GetRelativePath(extractionFolder, path))) +
                            "\n\nDie Programme werden einzeln und nacheinander gestartet. " +
                            "Bitte jeden Assistenten vollständig abschließen, bevor der nächste startet.\n\n" +
                            "Nur ausführen, wenn du den Dateien vertraust. Jetzt beginnen?",
                            "Mehrstufige Spielinstallation", MessageBoxButton.YesNo,
                            MessageBoxImage.Warning, MessageBoxResult.No);
                        if (confirmation != MessageBoxResult.Yes)
                        {
                            StatusText.Text = "Installation abgebrochen. Temporäre Dateien werden entfernt.";
                            return;
                        }
                        for (var step = 0; step < installers.Length; step++)
                        {
                            var path = installers[step];
                            var proceed = MessageBox.Show(this,
                                "Schritt " + (step + 1) + " von " + installers.Length +
                                "\n\n" + Path.GetRelativePath(extractionFolder, path) +
                                "\n\nDieses Installationsprogramm jetzt starten?",
                                "Installation: " + game.Name, MessageBoxButton.YesNo,
                                MessageBoxImage.Question, MessageBoxResult.No);
                            if (proceed != MessageBoxResult.Yes)
                            {
                                StatusText.Text = "Installation unterbrochen. Temporäre Dateien werden entfernt.";
                                return;
                            }
                            using var process = System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo(path)
                                {
                                    UseShellExecute = true,
                                    WorkingDirectory = Path.GetDirectoryName(path)!
                                }) ?? throw new IOException("Installer konnte nicht gestartet werden: " + path);
                            await process.WaitForExitAsync();
                            if (process.ExitCode != 0)
                            {
                                var continueAfterError = MessageBox.Show(this,
                                    "Der Installer meldete Exitcode " + process.ExitCode +
                                    ".\n\nTrotzdem mit dem nächsten Schritt fortfahren?",
                                    "Installer abgeschlossen", MessageBoxButton.YesNo,
                                    MessageBoxImage.Warning, MessageBoxResult.No);
                                if (continueAfterError != MessageBoxResult.Yes) return;
                            }
                        }
                        var installedExe = new OpenFileDialog
                        {
                            Title = "Spiel-EXE nach der Installation auswählen",
                            Filter = "Anwendungen (*.exe)|*.exe",
                            CheckFileExists = true,
                            InitialDirectory = Directory.Exists(targetFolder) ? targetFolder :
                                settings.InstallDirectory
                        };
                        if (installedExe.ShowDialog(this) == true)
                        {
                            settings.InstalledExecutables[game.Id] = installedExe.FileName;
                            settings.Save();
                            StatusText.Text = "Mehrstufige Installation abgeschlossen: " + game.Name;
                        }
                        else
                            StatusText.Text = "Installation abgeschlossen. Spiel-EXE bitte in den Einstellungen hinterlegen.";
                        return;
                    }
                    // Archives without installers represent portable games; move them into Games.
                    Directory.Move(extractionFolder, targetFolder);
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
                    throw;
                }
                finally
                {
                    if (Directory.Exists(extractionFolder))
                    {
                        try { Directory.Delete(extractionFolder, recursive: true); }
                        catch (Exception ex) { StatusText.Text = "Temporäre Installationsdateien konnten nicht vollständig entfernt werden: " + ex.Message; }
                    }
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
            settings.DownloadedPackages[game.Id] = outputPath;
            settings.Save();
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
