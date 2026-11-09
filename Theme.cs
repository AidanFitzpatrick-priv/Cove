using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cove;

static class Theme
{
    public static readonly SolidColorBrush Accent = new(Color.FromRgb(0xFF, 0x5A, 0x36));
    public static readonly SolidColorBrush Ink = new(Color.FromRgb(0xF2, 0xF0, 0xEC));
    public static readonly SolidColorBrush Muted = new(Color.FromRgb(0x8D, 0x8A, 0x84));
    public static readonly SolidColorBrush Shell = new(Color.FromRgb(0x12, 0x13, 0x16));
    public static readonly SolidColorBrush Panel = new(Color.FromRgb(0x16, 0x18, 0x1D));
    public static readonly SolidColorBrush Chrome = new(Color.FromRgb(0x18, 0x1A, 0x1F));
    public static readonly SolidColorBrush Field = new(Color.FromRgb(0x0E, 0x0F, 0x12));
    public static readonly SolidColorBrush Line = new(Color.FromRgb(0x2A, 0x2D, 0x34));
    public static readonly SolidColorBrush Hover = new(Color.FromRgb(0x24, 0x26, 0x2C));
    public static readonly SolidColorBrush Selected = new(Color.FromRgb(0x2E, 0x31, 0x38));
    public static readonly SolidColorBrush ButtonFace = new(Color.FromRgb(0x2A, 0x2D, 0x34));
    public static readonly SolidColorBrush TabIdle = new(Color.FromArgb(0x1A, 0xFF, 0x5A, 0x36));
    public static readonly SolidColorBrush TabActive = new(Color.FromArgb(0x88, 0xFF, 0x5A, 0x36));
    public static readonly SolidColorBrush TabHover = new(Color.FromArgb(0x4D, 0xFF, 0x5A, 0x36));

    public static string AccentHex { get; private set; } = "#FF5A36";
    public static string Appearance { get; private set; } = "dark";
    static bool _listening;

    public static readonly string[] Swatches =
    [
        "#FF5A36", "#FF4D6A", "#C084FC", "#60A5FA", "#34D399", "#FBBF24", "#F2F0EC"
    ];

    public static void Apply(string? hex)
    {
        if (!TryParse(hex, out var color, out var normalized)) return;
        AccentHex = normalized;
        Accent.Color = color;
        RefreshTabAccents();
    }

    static void RefreshTabAccents()
    {
        var accent = Accent.Color;
        var idleAlpha = (byte)(IsLight() ? 0x24 : 0x22);
        var hoverAlpha = (byte)(IsLight() ? 0x55 : 0x4D);
        var activeAlpha = (byte)(IsLight() ? 0x99 : 0x88);
        TabIdle.Color = Color.FromArgb(idleAlpha, accent.R, accent.G, accent.B);
        TabHover.Color = Color.FromArgb(hoverAlpha, accent.R, accent.G, accent.B);
        TabActive.Color = Color.FromArgb(activeAlpha, accent.R, accent.G, accent.B);
    }

    public static void ApplyAppearance(string? mode)
    {
        Appearance = mode is "light" or "system" ? mode : "dark";
        if (Application.Current is null) return;
        EnsureListening();
        Application.Current.RequestedThemeVariant = Appearance switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        RefreshBrushes();
    }

    static void EnsureListening()
    {
        if (_listening || Application.Current is null) return;
        _listening = true;
        Application.Current.ActualThemeVariantChanged += (_, _) =>
        {
            if (Appearance == "system") RefreshBrushes();
        };
    }

    static void RefreshBrushes()
    {
        var light = IsLight();
        if (light)
        {
            Ink.Color = Color.FromRgb(0x1C, 0x1A, 0x18);
            Muted.Color = Color.FromRgb(0x6E, 0x6B, 0x66);
            Shell.Color = Color.FromRgb(0xF4, 0xF2, 0xEE);
            Panel.Color = Color.FromRgb(0xEB, 0xE8, 0xE2);
            Chrome.Color = Color.FromRgb(0xE4, 0xE1, 0xDA);
            Field.Color = Color.FromRgb(0xFF, 0xFF, 0xFF);
            Line.Color = Color.FromRgb(0xD0, 0xCB, 0xC2);
            Hover.Color = Color.FromRgb(0xDD, 0xD8, 0xCF);
            Selected.Color = Color.FromRgb(0xD2, 0xCC, 0xC2);
            ButtonFace.Color = Color.FromRgb(0xD8, 0xD3, 0xCA);
        }
        else
        {
            Ink.Color = Color.FromRgb(0xF2, 0xF0, 0xEC);
            Muted.Color = Color.FromRgb(0x8D, 0x8A, 0x84);
            Shell.Color = Color.FromRgb(0x12, 0x13, 0x16);
            Panel.Color = Color.FromRgb(0x16, 0x18, 0x1D);
            Chrome.Color = Color.FromRgb(0x18, 0x1A, 0x1F);
            Field.Color = Color.FromRgb(0x0E, 0x0F, 0x12);
            Line.Color = Color.FromRgb(0x2A, 0x2D, 0x34);
            Hover.Color = Color.FromRgb(0x24, 0x26, 0x2C);
            Selected.Color = Color.FromRgb(0x2E, 0x31, 0x38);
            ButtonFace.Color = Color.FromRgb(0x2A, 0x2D, 0x34);
        }

        if (Application.Current?.Resources is { } resources)
        {
            resources["Ink"] = Ink;
            resources["Muted"] = Muted;
            resources["Shell"] = Shell;
            resources["Panel"] = Panel;
            resources["Chrome"] = Chrome;
            resources["Field"] = Field;
            resources["Line"] = Line;
            resources["Hover"] = Hover;
            resources["Selected"] = Selected;
            resources["ButtonFace"] = ButtonFace;
            resources["TabIdle"] = TabIdle;
            resources["TabActive"] = TabActive;
            resources["TabHover"] = TabHover;
        }
        RefreshTabAccents();
    }

    public static bool IsLight()
    {
        if (Appearance == "light") return true;
        if (Appearance == "dark") return false;
        return Application.Current?.ActualThemeVariant == ThemeVariant.Light;
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
