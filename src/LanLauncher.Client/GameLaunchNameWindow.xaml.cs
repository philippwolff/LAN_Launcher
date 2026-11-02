using System.Windows;

namespace LanLauncher.Client;

public partial class GameLaunchNameWindow : Window
{
    public string OptionName => NameBox.Text.Trim();

    public GameLaunchNameWindow(string suggestedName)
    {
        InitializeComponent();
        NameBox.Text = suggestedName;
        Loaded += (_, _) =>
        {
            ThemeManager.Apply(LauncherSettings.Load().DarkMode);
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OptionName))
        {
            MessageBox.Show(this, "Bitte einen Namen für die Startoption eingeben.",
                "Name fehlt", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
