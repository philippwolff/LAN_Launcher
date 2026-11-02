using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LanLauncher.Client;

internal static class ThemeManager
{
    private static readonly string[] BrushKeys = { "Win98Face", "Win98Shadow", "Win98Dark", "Win98Highlight", "Win98Blue" };
    internal static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;
        var colors = dark
            ? new[] { "#252830", "#454A55", "#15171C", "#737985", "#343B75" }
            : new[] { "#C0C0C0", "#808080", "#404040", "#FFFFFF", "#000080" };
        for (var i = 0; i < BrushKeys.Length; i++)
            if (resources[BrushKeys[i]] is SolidColorBrush brush)
                brush.Color = (Color)ColorConverter.ConvertFromString(colors[i]);

        var background = Brush(dark ? "#252830" : "#C0C0C0");
        var input = Brush(dark ? "#343945" : "#FFFFFF");
        var foreground = Brush(dark ? "#F2F3F6" : "#000000");
        var muted = Brush(dark ? "#9DA5B5" : "#808080");
        var border = Brush(dark ? "#5A6270" : "#808080");
        var hover = Brush(dark ? "#404755" : "#D4D4D4");
        var selection = Brush(dark ? "#45567C" : "#D6E8FF");

        SetWindow(background, foreground);
        SetControl<Button>(background, foreground, border);
        SetControl<TextBox>(input, foreground, border);
        SetControl<PasswordBox>(input, foreground, border);
        SetControl<ListBox>(input, foreground, border);
        SetControl<ComboBox>(input, foreground, border);
        SetControl<GroupBox>(background, foreground, border);
        SetControl<CheckBox>(background, foreground, border);
        SetControl<Expander>(background, foreground, border);

        // Existing application styles explicitly set colors; override their setters
        // to make all windows, including dialogs, follow the selected theme.
        if (resources[typeof(Button)] is Style buttonStyle)
        {
            foreach (var setter in buttonStyle.Setters.OfType<Setter>())
            {
                if (setter.Property == Control.BackgroundProperty) setter.Value = background;
                if (setter.Property == Control.ForegroundProperty) setter.Value = foreground;
            }
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static void SetWindow(Brush background, Brush foreground)
    {
        if (Application.Current.Resources[typeof(Window)] is Style style)
        {
            foreach (var setter in style.Setters.OfType<Setter>())
            {
                if (setter.Property == Control.BackgroundProperty) setter.Value = background;
                if (setter.Property == Control.ForegroundProperty) setter.Value = foreground;
            }
        }
        foreach (Window window in Application.Current.Windows)
        {
            window.Background = background;
            window.Foreground = foreground;
        }
    }

    private static void SetControl<T>(Brush background, Brush foreground, Brush border) where T : Control
    {
        if (Application.Current.Resources[typeof(T)] is not Style style) return;
        foreach (var setter in style.Setters.OfType<Setter>())
        {
            if (setter.Property == Control.BackgroundProperty) setter.Value = background;
            if (setter.Property == Control.ForegroundProperty) setter.Value = foreground;
            if (setter.Property == Control.BorderBrushProperty) setter.Value = border;
        }
    }
}
