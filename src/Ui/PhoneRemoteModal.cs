using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QRCoder;

namespace CouchLauncher.Ui;

/// <summary>A big QR code for guests to scan, with the address written under it.</summary>
sealed class PhoneRemoteModal : Modal
{
    public PhoneRemoteModal(MainWindow host) : base(host) { }

    public override FrameworkElement Build()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = Crt.Text("PHONE REMOTE", 50, bold: true);
        heading.Effect = Crt.Glow(20, 0.8);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Margin = new Thickness(0, 0, 0, 30);
        panel.Children.Add(heading);

        var urls = WebRemote.Running ? WebRemote.Urls() : new List<string>();
        if (urls.Count > 0)
        {
            var code = Qr(urls[0], 440);
            code.HorizontalAlignment = HorizontalAlignment.Center;
            code.Margin = new Thickness(0, 0, 0, 26);
            panel.Children.Add(code);
            foreach (var url in urls) panel.Children.Add(Centered(url, 40, 1, bold: true));
            panel.Children.Add(Centered("Scan with a phone on the same Wi-Fi, or type the address.", 22, 0.6));
            panel.Children.Add(Centered("If it won't load, allow Couch Launcher through Windows Firewall.", 22, 0.6));
        }
        else
        {
            string why = !Host.Config.PhoneRemote ? "SWITCHED OFF. ◄► IN SETTINGS TURNS IT ON."
                : WebRemote.Problem.Length > 0 ? WebRemote.Problem
                : "NOT CONNECTED TO A NETWORK";
            panel.Children.Add(Centered(why, 30, 1));
        }

        var hint = Centered("B BACK", 22, 0.55);
        hint.Margin = new Thickness(0, 30, 0, 0);
        panel.Children.Add(hint);
        return panel;
    }

    public override void OnNav(Nav nav)
    {
        if (nav is Nav.Select or Nav.Back) Host.CloseModal();
    }

    static TextBlock Centered(string text, double size, double alpha, bool bold = false)
    {
        var block = Crt.Text(text, size, alpha, bold);
        block.HorizontalAlignment = HorizontalAlignment.Center;
        block.Margin = new Thickness(0, 0, 0, 8);
        return block;
    }

    static Image Qr(string text, double size)
    {
        var matrix = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M).ModuleMatrix;
        int n = matrix.Count; // includes the blank border scanners need
        var modules = new GeometryGroup();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (matrix[y][x]) modules.Children.Add(new RectangleGeometry(new Rect(x, y, 1, 1)));

        // Phone cameras want dark squares on a light ground, so use whichever
        // theme colour is lighter as the paper, and fall back to plain
        // black on white when the theme's two colours are too close.
        Color fg = Crt.P.Phosphor, bg = Crt.P.Background;
        Color paper = Luma(fg) > Luma(bg) ? fg : bg, ink = paper == fg ? bg : fg;
        if (Luma(paper) - Luma(ink) < 0.45) (paper, ink) = (Colors.White, Colors.Black);

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(paper), null, new RectangleGeometry(new Rect(0, 0, n, n))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(ink), null, modules));
        drawing.Freeze();

        var image = new Image { Source = new DrawingImage(drawing), Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);
        return image;
    }

    static double Luma(Color c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255.0;
}
