using System.Windows;
using System.Windows.Controls;

namespace LanLauncher.Client;

public partial class InstallerOptionsWindow : Window
{
    public string InstallerType => (InstallerTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "manual";

    public InstallerOptionsWindow(string gameName, string targetFolder, string savedType, bool automaticallyDetected)
    {
        InitializeComponent();
        GameTitle.Text = "Installation von " + gameName;
        TargetPath.Text = targetFolder;
        if (automaticallyDetected)
        {
            foreach (var item in InstallerTypeBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag?.ToString() == savedType)
                    item.Content = item.Content + " (automatisch erkannt)";
            }
        }
        foreach (var item in InstallerTypeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == savedType)
            {
                InstallerTypeBox.SelectedItem = item;
                break;
            }
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
