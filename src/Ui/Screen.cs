using System.Windows;

namespace CouchLauncher.Ui;

/// <summary>A full-screen page under the header: home, switcher, settings, editor.</summary>
abstract class Screen
{
    protected readonly MainWindow Host;

    protected Screen(MainWindow host) { Host = host; }

    /// <summary>Called whenever it's shown and after a theme change, so it
    /// always paints with the current colours.</summary>
    public abstract FrameworkElement Build();

    /// <summary>The controls legend in the footer.</summary>
    public virtual string Hint => "";

    /// <summary>True when text fields need the keyboard (only Escape is taken).</summary>
    public virtual bool CapturesTyping => false;

    public virtual void OnNav(Nav nav) { }
    public virtual void Tick() { }
    public virtual void OnLeave() { }
}

/// <summary>A box over the current screen that takes every key until it's closed.</summary>
abstract class Modal
{
    protected readonly MainWindow Host;

    protected Modal(MainWindow host) { Host = host; }

    public abstract FrameworkElement Build();
    public abstract void OnNav(Nav nav);
}
