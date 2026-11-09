using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cove;

static class Prompts
{
    public static async Task<bool> Confirm(Window owner, string title, string message)
    {
        var win = Shell(owner, title, 360);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(16, 14, 16, 8), Foreground = Brushes.White };
        var no = new Button { Content = "Cancel", Margin = new Avalonia.Thickness(0, 0, 8, 0), MinWidth = 88 };
        var yes = new Button { Content = "Confirm", MinWidth = 88 };
        no.Click += (_, _) => win.Close(false);
        yes.Click += (_, _) => win.Close(true);
        win.Content = Column(text, Buttons(no, yes));
        return await win.ShowDialog<bool>(owner);
    }

    public static async Task<string?> Ask(Window owner, string title, string initial)
    {
        var win = Shell(owner, title, 380);
        var box = new TextBox { Text = initial, Margin = new Avalonia.Thickness(16, 16, 16, 8) };
        var cancel = new Button { Content = "Cancel", Margin = new Avalonia.Thickness(0, 0, 8, 0), MinWidth = 88 };
        var ok = new Button { Content = "Save", MinWidth = 88 };
        cancel.Click += (_, _) => win.Close(null);
        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) win.Close(box.Text.Trim());
        };
        win.Content = Column(box, Buttons(cancel, ok));
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await win.ShowDialog<string?>(owner);
    }

    static Window Shell(Window owner, string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = false,
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF0, 0xEC))
    };

    static StackPanel Column(Control body, Control buttons)
    {
        var panel = new StackPanel();
        panel.Children.Add(body);
        panel.Children.Add(buttons);
        return panel;
    }

    static StackPanel Buttons(Button left, Button right)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(16, 4, 16, 16)
        };
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }
}
