using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cove;

static class Theme
{
    public static readonly SolidColorBrush Accent = new(Color.FromRgb(0xFF, 0x5A, 0x36));
    public static readonly SolidColorBrush Ink = new(Color.FromRgb(0xF2, 0xF0, 0xEC));
    public static readonly SolidColorBrush Muted = new(Color.FromRgb(0x8D, 0x8A, 0x84));
    public static string AccentHex { get; private set; } = "#FF5A36";
    public static string Appearance { get; private set; } = "dark";

    public static readonly string[] Swatches =
    [
        "#FF5A36", "#FF4D6A", "#C084FC", "#60A5FA", "#34D399", "#FBBF24", "#F2F0EC"
    ];

    public static void Apply(string? hex)
    {
        if (!TryParse(hex, out var color, out var normalized)) return;
        AccentHex = normalized;
        Accent.Color = color;
    }

    public static void ApplyAppearance(string? mode)
    {
        Appearance = mode is "light" or "system" ? mode : "dark";
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = Appearance switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        if (Appearance == "light")
        {
            Ink.Color = Color.FromRgb(0x1C, 0x1A, 0x18);
            Muted.Color = Color.FromRgb(0x6E, 0x6B, 0x66);
        }
        else
        {
            Ink.Color = Color.FromRgb(0xF2, 0xF0, 0xEC);
            Muted.Color = Color.FromRgb(0x8D, 0x8A, 0x84);
        }
    }

    public static bool TryParse(string? hex, out Color color, out string normalized)
    {
        color = default;
        normalized = "";
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var text = hex.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (!Color.TryParse(text, out color)) return false;
        normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return true;
    }
}
