using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LanLauncher.Server.Gui;

public partial class RegisterGameWindow : Window
{
    private static readonly string[] Genres =
    [
        "Action", "Abenteuer", "Arcade", "Aufbau", "Battle Royale", "Beat 'em up",
        "Brettspiel", "Casual", "Dungeon Crawler", "Echtzeitstrategie", "Flugsimulation",
        "Geschicklichkeit", "Horror", "Jump 'n' Run", "Kartenspiel", "Koop",
        "MOBA", "MMORPG", "Musik / Rhythmus", "Open World", "Partyspiel",
        "Puzzle / Rätsel", "Racing / Rennspiel", "Rollenspiel (RPG)",
        "Roguelike / Roguelite", "Sandbox", "Shooter", "Simulation",
        "Sport", "Stealth", "Strategie", "Survival", "Taktik",
        "Tower Defense", "Visual Novel", "Wirtschaftssimulation",
        "Rundenstrategie", "4X-Strategie", "Sonstiges"
    ];

    public string FileName { get; }
    public string GameName => NameInput.Text.Trim();
    public string GameVersion => VersionInput.Text.Trim();
    public string GameDescription => DescriptionInput.Text.Trim();
    public int[] PlayerCounts => CountsPanel.Children.OfType<CheckBox>()
        .Where(c => c.IsChecked == true).Select(c => (int)c.Tag).ToArray();
    public string[] SelectedGenres => GenresPanel.Children.OfType<CheckBox>()
        .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();

    public RegisterGameWindow(string filename)
    {
        InitializeComponent();
        FileName = filename;
        FileLabel.Text = filename;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var separator = stem.LastIndexOf("--", StringComparison.Ordinal);
        NameInput.Text = separator >= 0 ? stem[..separator] : stem;
        VersionInput.Text = "1.0";
        for (var i = 1; i <= 16; i++)
            CountsPanel.Children.Add(new CheckBox { Content = i.ToString(), Tag = i, Margin = new Thickness(0, 0, 13, 8), Foreground = System.Windows.Media.Brushes.White });
        foreach (var genre in Genres)
            GenresPanel.Children.Add(new CheckBox { Content = genre, Tag = genre, Width = 235, Margin = new Thickness(0, 0, 0, 8), Foreground = System.Windows.Media.Brushes.White });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (GameName.Length == 0 || GameVersion.Length == 0)
        {
            MessageBox.Show(this, "Bitte Spielname und Version eingeben.", "Pflichtfelder");
            return;
        }
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
