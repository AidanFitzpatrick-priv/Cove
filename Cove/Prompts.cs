using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cove;

static class Prompts
{
    public static bool Confirm(Window owner, string title, string message)
    {
        var win = Shell(owner, title, 360);
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 14, 16, 8),
            Foreground = Brushes.White
        };
        var no = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true, MinWidth = 88 };
        var yes = new Button { Content = "Confirm", IsDefault = true, MinWidth = 88 };
        yes.Click += (_, _) => { win.DialogResult = true; };
        win.Content = Column(text, Buttons(no, yes));
        return win.ShowDialog() == true;
    }

    public static string? Ask(Window owner, string title, string initial)
    {
        var win = Shell(owner, title, 380);
        var box = new TextBox { Text = initial, Margin = new Thickness(16, 16, 16, 8), Padding = new Thickness(10, 8, 10, 8) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true, MinWidth = 88 };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 88 };
        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text)) win.DialogResult = true;
        };
        win.Content = Column(box, Buttons(cancel, ok));
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }

    static Window Shell(Window owner, string title, double width) => new()
    {
        Owner = owner,
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        WindowStyle = WindowStyle.ToolWindow,
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF0, 0xEC))
    };

    static StackPanel Column(UIElement body, UIElement buttons)
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
            Margin = new Thickness(16, 4, 16, 16)
        };
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }
}
