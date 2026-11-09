using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cove;

abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

sealed class TabVm : NotifyObject
{
    string _title = "New tab";
    string _url = "";
    Bitmap? _favicon;
    bool _sleeping;
    int _blocked;

    public TabVm(PageSession page) => Page = page;
    public string Id { get; } = Guid.NewGuid().ToString("n");
    public PageSession Page { get; }
    public long LastActive { get; set; }
    public DateTimeOffset Seen { get; set; }
    public string PageHost { get; set; } = "";

    public string Title { get => _title; set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "New tab" : value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public Bitmap? Favicon { get => _favicon; set => Set(ref _favicon, value); }
    public bool IsSleeping { get => _sleeping; set => Set(ref _sleeping, value); }
    public int Blocked { get => _blocked; set => Set(ref _blocked, value); }
}

sealed class WorkspaceVm : NotifyObject
{
    string _name;
    string _colorHex;
    Brush _swatch;
    bool _isActive;

    public WorkspaceVm(string name, string colorHex)
    {
        _name = name;
        _colorHex = colorHex;
        _swatch = MakeBrush(colorHex);
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public int ActiveIndex { get; set; }
    public ObservableCollection<TabVm> Tabs { get; } = new();
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public string ColorHex => _colorHex;
    public Brush Swatch => _swatch;

    public void SetColor(string hex)
    {
        _colorHex = hex;
        _swatch = MakeBrush(hex);
        Raise(nameof(ColorHex));
        Raise(nameof(Swatch));
    }

    static Brush MakeBrush(string hex)
    {
        Theme.TryParse(hex, out var color, out _);
        return new SolidColorBrush(color);
    }
}

sealed class DownloadVm : NotifyObject
{
    string _detail = "";
    double _progress;
    bool _indeterminate = true;
    bool _running = true;

    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Total { get; set; }
    public int DownloadId { get; set; }
    public Action? CancelDownload { get; set; }
    public bool Settled { get; set; }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }
}

sealed class AppState
{
    public string Accent { get; set; } = "#FF5A36";
    public string Appearance { get; set; } = "dark";
    public bool AddressAutocomplete { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public int RamCapMb { get; set; } = 4096;
    public bool AdBlockEnabled { get; set; } = true;
    public bool SidebarOpen { get; set; } = true;
    public string SidebarDock { get; set; } = "left";
    public double SidebarSize { get; set; } = 188;
    public bool Maximized { get; set; }
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public string? ActiveWorkspaceId { get; set; }
    public List<string> AdBlockAllow { get; set; } = [];
    public List<string> NeverSave { get; set; } = [];
    public List<WorkspaceState> Workspaces { get; set; } = [];
    public List<Bookmark> Bookmarks { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public List<DownloadRecord> Downloads { get; set; } = [];
    public List<string> ExtensionPaths { get; set; } = [];
}

sealed class WorkspaceState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "Main";
    public string Color { get; set; } = "#FF5A36";
    public int ActiveIndex { get; set; }
    public List<TabState> Tabs { get; set; } = [];
}

sealed class TabState
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "New tab";
    public DateTimeOffset Seen { get; set; }
}

sealed class Bookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
}

sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset Visited { get; set; }
}

sealed class DownloadRecord
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTimeOffset When { get; set; }
}

sealed class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
