using System.Windows;

namespace LanLauncher.Client;

public partial class NasCredentialsWindow : Window
{
    public string Password => NasPassword.Password;
    public bool SaveInWindows => Remember.IsChecked == true;

    public NasCredentialsWindow(string share, bool admin)
    {
        InitializeComponent();
        Explanation.Text = "Die Verbindung zu " + share + " benötigt das Passwort für " +
            (admin ? "launcher_admin" : "launcher_reader") + ".";
        NasPassword.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(NasPassword.Password))
        {
            MessageBox.Show(this, "Bitte das NAS-Passwort eingeben.", "NAS-Anmeldung");
            return;
        }
        DialogResult = true;
    }
}