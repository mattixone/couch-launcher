using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace CouchLauncher.Ui;

/// <summary>
/// The CRT look, after the phone app's CRTUi.kt: phosphor-coloured mono text
/// on a flat panel, a soft bloom on whatever has focus, scanlines over it all.
/// </summary>
static class Crt
{
    public static Theme P { get; private set; } = Theme.All[0];
    public static readonly FontFamily Mono = new("Consolas, Courier New");
    public static readonly FontFamily Symbols = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static void Use(Theme theme) => P = theme;

    public static SolidColorBrush Fg(double alpha = 1) => Solid(P.Phosphor, alpha);
    public static SolidColorBrush Bg(double alpha = 1) => Solid(P.Background, alpha);

    static SolidColorBrush Solid(Color c, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    public static TextBlock Text(string text, double size, double alpha = 1, bool bold = false)
    {
        var block = new TextBlock { Text = text };
        Style(block, size, alpha, bold);
        return block;
    }

    public static void Style(TextBlock block, double size, double alpha = 1, bool bold = false)
    {
        block.FontFamily = Mono;
        block.FontSize = size;
        block.Foreground = Fg(alpha);
        block.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
    }

    public static Effect Glow(double radius, double opacity) =>
        new DropShadowEffect { Color = P.Phosphor, BlurRadius = radius, ShadowDepth = 0, Opacity = opacity };

    /// <summary>Outlined command button, like the phone's CRTButton. Mouse-driven.</summary>
    public static Border Button(string label, Action onClick, bool selected = false, bool enabled = true, double size = 24)
    {
        double rest = selected ? 0.18 : 0.04;
        var button = new Border
        {
            Child = Text(label, size, enabled ? 1 : 0.35, bold: selected),
            BorderBrush = Fg(!enabled ? 0.25 : selected ? 1 : 0.55),
            BorderThickness = new Thickness(2),
            Background = Fg(rest),
            Padding = new Thickness(18, 10, 18, 10),
            Margin = new Thickness(0, 0, 14, 14),
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
        };
        if (selected) button.Effect = Glow(16, 0.6);
        if (enabled)
        {
            button.MouseEnter += (_, _) => button.Background = Fg(rest + 0.1);
            button.MouseLeave += (_, _) => button.Background = Fg(rest);
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
        }
        return button;
    }

    /// <summary>Text field in the theme's colours (the stock one turns blue on focus).</summary>
    public static TextBox Input(string value, Action<string> onChange)
    {
        var box = new TextBox
        {
            Text = value,
            FontFamily = Mono,
            FontSize = 24,
            Foreground = Fg(),
            CaretBrush = Fg(),
            SelectionBrush = Fg(0.5),
            Background = Fg(0.07),
            BorderBrush = Fg(0.5),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(12, 8, 12, 8),
            Template = InputTemplate(),
        };
        box.TextChanged += (_, _) => onChange(box.Text);
        box.GotKeyboardFocus += (_, _) => box.BorderBrush = Fg();
        box.LostKeyboardFocus += (_, _) => box.BorderBrush = Fg(0.5);
        return box;
    }

    static ControlTemplate? inputTemplate;

    static ControlTemplate InputTemplate()
    {
        if (inputTemplate != null) return inputTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(host);
        inputTemplate = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        inputTemplate.Seal();
        return inputTemplate;
    }

    /// <summary>One dark line every 3 units, tiled — ScanlineOverlay's pattern.</summary>
    public static Brush Scanlines(double opacity)
    {
        var pixels = new byte[4 * 3];
        pixels[3] = (byte)Math.Round(255 * opacity);
        var bitmap = BitmapSource.Create(1, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        return Tiled(bitmap, 1, 3);
    }

    /// <summary>Faint static grain for the pale LCD panels.</summary>
    public static Brush Noise()
    {
        const int size = 160;
        var random = new Random(7);
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte shade = (byte)(random.Next(2) * 255);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
            pixels[i + 3] = (byte)random.Next(0, 22);
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        return Tiled(bitmap, size, size);
    }

    static Brush Tiled(BitmapSource bitmap, double width, double height)
    {
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            Viewport = new Rect(0, 0, width, height),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }
}
