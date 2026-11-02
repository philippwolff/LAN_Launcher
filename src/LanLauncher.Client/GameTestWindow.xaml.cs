using System.Windows;

namespace LanLauncher.Client;

public partial class GameTestWindow : Window
{
    public bool Tested => TestedBox.IsChecked == true;
    public string OperatingSystem => OsBox.Text.Trim();

    public GameTestWindow(bool tested, string operatingSystem)
    {
        InitializeComponent();
        TestedBox.IsChecked = tested;
        OsBox.Text = operatingSystem;
        OsBox.IsEnabled = tested;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        ThemeManager.Apply(LauncherSettings.Load().DarkMode);

    private void Tested_Changed(object sender, RoutedEventArgs e)
    {
        if (OsBox is not null) OsBox.IsEnabled = Tested;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Tested && string.IsNullOrWhiteSpace(OperatingSystem))
        {
            MessageBox.Show(this, "Bitte das Betriebssystem angeben.", "Spieltest",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
