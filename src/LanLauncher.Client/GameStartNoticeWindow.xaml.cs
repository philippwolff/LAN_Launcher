using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameStartNoticeWindow : Window
{
    public string Notice => NoticeBox.Text;

    public GameStartNoticeWindow(GameInfo game, string current)
    {
        InitializeComponent();
        GameTitle.Text = game.Name;
        NoticeBox.Text = current;
        NoticeBox.Focus();
        NoticeBox.CaretIndex = NoticeBox.Text.Length;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
