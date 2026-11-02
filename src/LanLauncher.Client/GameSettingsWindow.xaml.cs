using System.IO;
using System.Windows;
using Microsoft.Win32;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameSettingsWindow : Window
{
    private readonly string installFolder;
    private readonly string gameName;
    private readonly List<GameLaunchOption> extraExecutables = new();
    public IReadOnlyList<GameLaunchOption> AdditionalExecutables => extraExecutables;
    public string ExecutablePath { get; private set; } = "";

    public GameSettingsWindow(GameInfo game, LauncherSettings settings)
    {
        InitializeComponent();
        gameName = game.Name;
        GameTitle.Text = game.Name + " (Version " + game.Version + ")";
        installFolder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        InstallFolderText.Text = "Installationsordner: " + installFolder;
        if (settings.AdditionalExecutables.TryGetValue(game.Id, out var options))
            extraExecutables.AddRange(options);
        RefreshExtras();
        if (settings.InstalledExecutables.TryGetValue(game.Id, out var existing))
            ExecutablePathBox.Text = existing;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var currentFolder = Path.GetDirectoryName(ExecutablePathBox.Text);
        var initial = currentFolder is not null && Directory.Exists(currentFolder)
            ? currentFolder
            : Directory.Exists(installFolder) ? installFolder : null;
        var picker = new OpenFileDialog
        {
            Title = "Spiel-EXE auswählen",
            Filter = "Anwendungen (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (initial is not null) picker.InitialDirectory = initial;
        if (picker.ShowDialog(this) == true) ExecutablePathBox.Text = picker.FileName;
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var detected = GameExecutableFinder.FindBest(installFolder, gameName);
        if (detected is null)
        {
            MessageBox.Show(this, "Keine eindeutige Spiel-EXE im Installationsordner gefunden.",
                "Automatische Suche", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExecutablePathBox.Text = detected;
    }

    private void RefreshExtras()
    {
        ExtraExecutablesList.ItemsSource = null;
        ExtraExecutablesList.ItemsSource = extraExecutables;
    }

    private void AddExtra_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Zusätzliche Spiel-EXE auswählen",
            Filter = "Anwendungen (*.exe)|*.exe",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(installFolder) ? installFolder :
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (picker.ShowDialog(this) != true) return;
        if (extraExecutables.Any(x => string.Equals(x.ExecutablePath, picker.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Diese EXE ist bereits als Startoption vorhanden.");
            return;
        }
        var defaultName = Path.GetFileNameWithoutExtension(picker.FileName);
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            "Anzeigename für diese Startoption:", "Startoption hinzufügen", defaultName).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        extraExecutables.Add(new GameLaunchOption(name, picker.FileName));
        RefreshExtras();
    }

    private void RemoveExtra_Click(object sender, RoutedEventArgs e)
    {
        if (ExtraExecutablesList.SelectedItem is not GameLaunchOption option) return;
        extraExecutables.Remove(option);
        RefreshExtras();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var value = ExecutablePathBox.Text.Trim();
        if (!string.IsNullOrEmpty(value) && (!File.Exists(value) ||
            !string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Bitte eine vorhandene EXE-Datei auswählen.",
                "Ungültige Spiel-EXE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ExecutablePath = value;
        DialogResult = true;
    }
}
