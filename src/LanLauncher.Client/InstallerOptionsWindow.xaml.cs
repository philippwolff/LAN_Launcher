using System.Windows;
using System.Windows.Controls;

namespace LanLauncher.Client;

public partial class InstallerOptionsWindow : Window
{
    public string InstallerType => (InstallerTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "manual";

    public InstallerOptionsWindow(string gameName, string targetFolder, string savedType)
    {
        InitializeComponent();
        GameTitle.Text = "Installation von " + gameName;
        TargetPath.Text = targetFolder;
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
