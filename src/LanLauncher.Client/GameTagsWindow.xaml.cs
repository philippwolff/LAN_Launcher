using System.Windows;
using System.Windows.Controls;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameTagsWindow : Window
{
    public GameTags Tags { get; private set; } = new([], []);

    public GameTagsWindow(string title, GameTags? current = null)
    {
        InitializeComponent();
        GameTitle.Text = title;
        current ??= new GameTags([], []);
        for (var count = 1; count <= 16; count++)
        {
            PlayerCountPanel.Children.Add(new CheckBox
            {
                Content = count.ToString(),
                Tag = count,
                IsChecked = current.PlayerCounts.Contains(count),
                Width = 65,
                Margin = new Thickness(0, 0, 8, 8)
            });
        }
        foreach (var genre in GameTagOptions.Genres)
        {
            GenrePanel.Children.Add(new CheckBox
            {
                Content = genre,
                IsChecked = current.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase),
                Width = 190,
                Margin = new Thickness(0, 0, 8, 10)
            });
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Tags = new GameTags(
            PlayerCountPanel.Children.OfType<CheckBox>().Where(x => x.IsChecked == true)
                .Select(x => (int)x.Tag).ToArray(),
            GenrePanel.Children.OfType<CheckBox>().Where(x => x.IsChecked == true)
                .Select(x => x.Content.ToString()!).ToArray());
        DialogResult = true;
    }
}
