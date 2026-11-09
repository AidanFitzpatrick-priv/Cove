using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cove;

sealed class VaultWindow : Window
{
    readonly Vault _vault;
    readonly TextBox _password = new() { PasswordChar = '•', Watermark = "Password" };
    readonly TextBox _confirm = new() { PasswordChar = '•', Watermark = "Type it again" };
    readonly TextBlock _message = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    VaultWindow(Vault vault)
    {
        _vault = vault;
        Title = "Passwords";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D));
        _confirm.IsVisible = !vault.Exists;
        var button = new Button
        {
            Content = vault.Exists ? "Unlock" : "Create password",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
        };
        button.Click += (_, _) => Unlock();
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Passwords", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = vault.Exists ? "Cove password" : "Choose a password for this Mac", Foreground = Brushes.White },
                _password,
                _confirm,
                _message,
                button
            }
        };
        Opened += (_, _) => _password.Focus();
    }

    public static async Task<bool> Ensure(Window owner, Vault vault)
    {
        if (vault.IsUnlocked) return true;
        var win = new VaultWindow(vault);
        return await win.ShowDialog<bool>(owner) && vault.IsUnlocked;
    }

    void Unlock()
    {
        var password = _password.Text ?? "";
        if (password.Length < 4)
        {
            _message.Text = "Use at least 4 characters.";
            return;
        }
        if (!_vault.Exists)
        {
            if (password != (_confirm.Text ?? ""))
            {
                _message.Text = "Those passwords do not match.";
                return;
            }
            _vault.Create(password);
        }
        else if (!_vault.Unlock(password))
        {
            _message.Text = "Wrong password.";
            return;
        }
        Close(true);
    }
}
