using Avalonia.Media;

namespace Cove;

static class Theme
{
    public static readonly SolidColorBrush Accent = new(Color.FromRgb(0xFF, 0x5A, 0x36));
    public static readonly SolidColorBrush Ink = new(Color.FromRgb(0xF2, 0xF0, 0xEC));
    public static readonly SolidColorBrush Muted = new(Color.FromRgb(0x8D, 0x8A, 0x84));
    public static string AccentHex { get; private set; } = "#FF5A36";

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
