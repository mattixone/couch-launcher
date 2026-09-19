using System.Windows;
using System.Windows.Controls;

namespace CouchLauncher.Ui;

/// <summary>A question with a short list of answers — confirms and the power menu.</summary>
sealed class ChoiceModal : Modal
{
    readonly string title;
    readonly string[] options;
    readonly Action<int> onPick;
    readonly List<Border> rows = new();
    int selected;

    public ChoiceModal(MainWindow host, string title, string[] options, Action<int> onPick) : base(host)
    {
        this.title = title;
        this.options = options;
        this.onPick = onPick;
    }

    public override FrameworkElement Build()
    {
        rows.Clear();
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var heading = Crt.Text(title, 50, bold: true);
        heading.Effect = Crt.Glow(20, 0.8);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Margin = new Thickness(0, 0, 0, 40);
        panel.Children.Add(heading);

        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var row = new Border
            {
                Width = 720,
                Padding = new Thickness(28, 16, 28, 16),
                Margin = new Thickness(0, 0, 0, 16),
                BorderThickness = new Thickness(2),
                Child = Crt.Text(options[i], 34, bold: true),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            row.MouseMove += (_, _) =>
            {
                if (selected != index && Input.CursorMoved())
                {
                    selected = index;
                    Paint();
                }
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Pick(index);
            };
            rows.Add(row);
            panel.Children.Add(row);
        }

        var hint = Crt.Text("A CHOOSE     B CANCEL", 22, 0.55);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 20, 0, 0);
        panel.Children.Add(hint);
        Paint();
        return panel;
    }

    public override void OnNav(Nav nav)
    {
        switch (nav)
        {
            case Nav.Up or Nav.Left: selected = Math.Max(0, selected - 1); Paint(); break;
            case Nav.Down or Nav.Right: selected = Math.Min(options.Length - 1, selected + 1); Paint(); break;
            case Nav.Select: Pick(selected); break;
            case Nav.Back: Host.CloseModal(); break;
        }
    }

    void Pick(int index)
    {
        // Close first: the answer may well open the next question.
        Host.CloseModal();
        onPick(index);
    }

    void Paint()
    {
        for (int i = 0; i < rows.Count; i++)
        {
            bool on = i == selected;
            rows[i].BorderBrush = Crt.Fg(on ? 1 : 0.3);
            rows[i].Background = Crt.Fg(on ? 0.18 : 0.03);
            rows[i].Effect = on ? Crt.Glow(26, 0.8) : null;
            ((TextBlock)rows[i].Child).Text = (on ? "▸ " : "  ") + options[i];
        }
    }
}

/// <summary>A long scrolling list to pick one from — installed apps, open windows.</summary>
sealed class PickerModal : Modal
{
    readonly string title;
    readonly List<string> items;
    readonly Action<int> onPick;
    readonly List<Border> rows = new();
    int selected;

    public PickerModal(MainWindow host, string title, List<string> items, Action<int> onPick) : base(host)
    {
        this.title = title;
        this.items = items;
        this.onPick = onPick;
    }

    public override FrameworkElement Build()
    {
        rows.Clear();
        var list = new StackPanel();
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var row = new Border
            {
                Padding = new Thickness(18, 10, 18, 10),
                Child = Crt.Text(items[i], 24),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            row.MouseMove += (_, _) =>
            {
                if (selected != index && Input.CursorMoved())
                {
                    selected = index;
                    Paint(scroll: false);
                }
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Pick(index);
            };
            rows.Add(row);
            list.Children.Add(row);
        }
        if (items.Count == 0) list.Children.Add(Crt.Text("NOTHING FOUND", 26, 0.7));

        var layout = new DockPanel();
        var heading = Crt.Text(title, 36, bold: true);
        heading.Margin = new Thickness(0, 0, 0, 20);
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(Crt.Button("CANCEL", Host.CloseModal));
        var tip = Crt.Text("CLICK ONE · MOUSE WHEEL SCROLLS", 20, 0.55);
        tip.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(tip);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });

        Paint(scroll: false);
        return new Border
        {
            Width = 1300,
            Height = 860,
            Padding = new Thickness(36),
            BorderThickness = new Thickness(2),
            BorderBrush = Crt.Fg(),
            Background = Crt.Bg(),
            Child = layout,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public override void OnNav(Nav nav)
    {
        switch (nav)
        {
            case Nav.Up: selected = Math.Max(0, selected - 1); Paint(); break;
            case Nav.Down: selected = Math.Min(items.Count - 1, selected + 1); Paint(); break;
            case Nav.Select: if (items.Count > 0) Pick(selected); break;
            case Nav.Back: Host.CloseModal(); break;
        }
    }

    void Pick(int index)
    {
        Host.CloseModal();
        onPick(index);
    }

    void Paint(bool scroll = true)
    {
        for (int i = 0; i < rows.Count; i++) rows[i].Background = Crt.Fg(i == selected ? 0.2 : 0);
        if (scroll && selected < rows.Count) rows[selected].BringIntoView();
    }
}
