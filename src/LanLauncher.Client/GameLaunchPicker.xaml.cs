using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameLaunchPicker : Window
{
    public GameLaunchOption? SelectedOption => OptionsList.SelectedItem as GameLaunchOption;
    public GameLaunchPicker(string gameName, IEnumerable<GameLaunchOption> options)
    {
        InitializeComponent();
        Title = gameName + " – Startoption wählen";
        OptionsList.ItemsSource = options.ToArray();
        OptionsList.SelectedIndex = 0;
        Loaded += (_, _) => ThemeManager.Apply(LauncherSettings.Load().DarkMode);
    }
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOption is not null) DialogResult = true;
    }
}
