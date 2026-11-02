using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LanLauncher.Client;

internal static class ThemeManager
{
    internal static void Apply(bool dark)
    {
        var background = Brush(dark ? "#252830" : "#C0C0C0");
        var input = Brush(dark ? "#343945" : "#FFFFFF");
        var foreground = Brush(dark ? "#F2F3F6" : "#000000");
        var border = Brush(dark ? "#5A6270" : "#808080");
        var selection = Brush(dark ? "#45567C" : "#D6E8FF");

        foreach (Window window in Application.Current.Windows)
        {
            window.Background = background;
            window.Foreground = foreground;
            ApplyChildren(window, dark, background, input, foreground, border, selection);
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static void ApplyChildren(DependencyObject parent, bool dark, Brush background,
        Brush input, Brush foreground, Brush border, Brush selection)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Control control)
            {
                if (control is TextBox or PasswordBox or ListBox or ComboBox)
                    control.Background = input;
                else if (control is Button or GroupBox or CheckBox or Expander)
                    control.Background = background;
                if (control is not ListBoxItem)
                    control.Foreground = foreground;
                if (control is Button or TextBox or PasswordBox or ListBox or ComboBox or GroupBox)
                    control.BorderBrush = border;
                if (control is ListBoxItem item)
                {
                    item.Background = item.IsSelected ? selection : input;
                    item.Foreground = foreground;
                }
            }
            else if (child is TextBlock text && !text.Name.Equals("LauncherVersionText", StringComparison.Ordinal))
            {
                // Preserve explicit white text in the blue header, but adapt ordinary text.
                if (text.Foreground is SolidColorBrush brush &&
                    brush.Color == Colors.Black)
                    text.Foreground = foreground;
            }
            ApplyChildren(child, dark, background, input, foreground, border, selection);
        }
    }
}
