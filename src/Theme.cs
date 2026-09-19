using System.Windows.Media;

namespace CouchLauncher;

/// <summary>
/// The phone app's colour themes (CRTTheme.kt), value for value, so the TV
/// and the Retroid can match.
/// </summary>
sealed record Theme(string Id, string Label, Color Phosphor, Color Background, double Scanlines, bool Noise)
{
    public static readonly Theme[] All =
    {
        new("FALLOUT", "FALLOUT 84", C(0xFF00FF66), C(0xFF000000), 0.33, false),
        new("AMBER", "AMBER ALERT", C(0xFFFFB000), C(0xFF000000), 0.33, false),
        new("CYBER", "CYBER-BRUTALIST", C(0xFF00FFFF), C(0xFF000000), 0.33, false),
        new("MATRIX", "MATRIX WHITE", C(0xFFFFFFFF), C(0xFF000000), 0.33, false),
        new("RED_ALERT", "RED ALERT", C(0xFFF0113A), C(0xFF000000), 0.33, false),
        new("HOT_PINK", "HOT PINK", C(0xFFF59BFF), C(0xFF000000), 0.33, false),
        new("DEEP_PURPLE", "DEEP PURPLE", C(0xFFA855F7), C(0xFF000000), 0.33, false),
        // Reflective LCDs: dark ink on a pale panel, grain instead of scanlines.
        new("CGA_LCD", "CGA LCD", C(0xFF2E3A24), C(0xFF9DA982), 0, true),
        new("SYNDICATE_LCD", "SYNDICATE LCD", C(0xFF1F2A3A), C(0xFF8D97A8), 0, true),
        // Backlit LCDs: bright segments on a tinted dark panel.
        new("WHITE_LED_LCD", "WHITE LED LCD", C(0xFF9FC5FF), C(0xFF0E1E33), 0, false),
        new("RED_LED_LCD", "RED LED LCD", C(0xFFFF3B30), C(0xFF1A0606), 0, false),
        new("YG_BLUE_LCD", "Y/G BLUE LCD", C(0xFF148003), C(0xFF0A1B2E), 0, false),
        new("LAVENDER_MOSS", "LAVENDER MOSS", C(0xFFA183C5), C(0xFF052009), 0, false),
    };

    /// <summary>Pale panel with dark ink — icons are tinted the other way round.</summary>
    public bool LightPanel => (Background.R * 0.299 + Background.G * 0.587 + Background.B * 0.114) / 255.0 > 0.4;

    public Theme Next => All[(Index + 1) % All.Length];
    public Theme Previous => All[(Index - 1 + All.Length) % All.Length];
    int Index => Array.IndexOf(All, this);

    public static Theme Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    static Color C(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
