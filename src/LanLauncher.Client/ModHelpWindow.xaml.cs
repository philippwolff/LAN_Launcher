using System.Windows;

namespace LanLauncher.Client;

public partial class ModHelpWindow : Window
{
    public ModHelpWindow() => InitializeComponent();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
