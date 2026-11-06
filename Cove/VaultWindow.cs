using System.Windows;
using System.Windows.Controls;

namespace Cove;

sealed class VaultWindow : Window
{
    readonly Vault _vault;
    readonly Func<VaultEntry, Task>? _fill;
    readonly bool _unlockOnly;
    readonly ListBox _list = new();
    readonly PasswordBox _password = new() { Margin = new Thickness(0, 8, 0, 8) };
    readonly PasswordBox _confirm = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock _message = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _lockPanel = new();
    readonly StackPanel _listPanel = new();

    VaultWindow(Vault vault, Func<VaultEntry, Task>? fill, bool unlockOnly)
    {
        _vault = vault;
        _fill = fill;
        _unlockOnly = unlockOnly;
        Title = "Passwords";
        Width = 420;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x18, 0x1D));
        Build();
        Refresh();
    }

    public static bool Ensure(Window owner, Vault vault)
    {
        if (vault.IsUnlocked) return true;
        var win = new VaultWindow(vault, null, unlockOnly: true) { Owner = owner };
        return win.ShowDialog() == true && vault.IsUnlocked;
    }

    public static void Manage(Window owner, Vault vault, Func<VaultEntry, Task> fill)
    {
        var win = new VaultWindow(vault, fill, unlockOnly: false) { Owner = owner };
        win.ShowDialog();
    }

    void Build()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock { Text = "Passwords", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(title);

        _lockPanel.Children.Add(new TextBlock { Text = "Master password", Foreground = System.Windows.Media.Brushes.White });
        _lockPanel.Children.Add(_password);
        _lockPanel.Children.Add(_confirm);
        _lockPanel.Children.Add(_message);
        var unlock = new Button { Content = "Unlock", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), IsDefault = true };
        unlock.Click += Unlock_Click;
        _lockPanel.Children.Add(unlock);
        Grid.SetRow(_lockPanel, 1);
        root.Children.Add(_lockPanel);

        _list.DisplayMemberPath = "Label";
        _listPanel.Children.Add(_list);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        actions.Children.Add(Action("Fill", Fill_Click));
        actions.Children.Add(Action("Copy password", Copy_Click));
        actions.Children.Add(Action("Delete", Delete_Click));
        _listPanel.Children.Add(actions);

        var host = Field("Site");
        var user = Field("Username");
        var pass = new PasswordBox { Margin = new Thickness(0, 4, 0, 8) };
        var add = new Button { Content = "Add", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(host.Text) || pass.Password.Length == 0)
            {
                _message.Text = "A site and password are required.";
                return;
            }
            _vault.Add(host.Text.Trim(), user.Text.Trim(), pass.Password);
            host.Text = "";
            user.Text = "";
            pass.Password = "";
            _message.Text = "";
            ReloadList();
        };
        _listPanel.Children.Add(new TextBlock { Text = "Add a password", Margin = new Thickness(0, 8, 0, 4) });
        _listPanel.Children.Add(host);
        _listPanel.Children.Add(user);
        _listPanel.Children.Add(pass);
        _listPanel.Children.Add(add);
        _listPanel.Children.Add(_message);
        Grid.SetRow(_listPanel, 1);
        root.Children.Add(_listPanel);
        Content = root;
    }

    static Button Action(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        return button;
    }

    static TextBox Field(string placeholder) => new() { Margin = new Thickness(0, 4, 0, 4), Tag = placeholder };

    void Refresh()
    {
        var locked = !_vault.IsUnlocked;
        _lockPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        _listPanel.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        _confirm.Visibility = _vault.Exists ? Visibility.Collapsed : Visibility.Visible;
        if (_lockPanel.Children[4] is Button button) button.Content = _vault.Exists ? "Unlock" : "Create password";
        if (!locked) ReloadList();
    }

    void ReloadList()
    {
        _list.ItemsSource = null;
        _list.ItemsSource = _vault.Entries.Select(e => new Row(e)).ToList();
    }

    void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var password = _password.Password;
        if (password.Length < 4)
        {
            _message.Text = "Use at least 4 characters.";
            return;
        }
        if (!_vault.Exists)
        {
            if (password != _confirm.Password)
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

        _password.Password = "";
        _confirm.Password = "";
        _message.Text = "";
        if (_unlockOnly)
        {
            DialogResult = true;
            return;
        }
        Refresh();
    }

    Row? Selected() => _list.SelectedItem as Row;

    async void Fill_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected();
        if (row is null || _fill is null) return;
        await _fill(row.Entry);
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected();
        if (row is null) return;
        try { Clipboard.SetText(row.Entry.Password); _message.Text = "Password copied."; }
        catch { _message.Text = "Could not copy."; }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected();
        if (row is null) return;
        _vault.Delete(row.Entry.Id);
        ReloadList();
    }

    sealed class Row
    {
        public Row(VaultEntry entry) => Entry = entry;
        public VaultEntry Entry { get; }
        public string Label => string.IsNullOrWhiteSpace(Entry.Username) ? Entry.Host : $"{Entry.Host}  ·  {Entry.Username}";
    }
}
