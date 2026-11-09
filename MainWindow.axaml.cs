using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Cove;

public partial class MainWindow : Window
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15");
        return http;
    }

    readonly bool _private;
    readonly AppState _state;
    readonly Vault _vault;
    readonly ObservableCollection<WorkspaceVm> _workspaces = new();
    readonly ObservableCollection<Bookmark> _bookmarks;
    readonly ObservableCollection<DownloadVm> _downloads = new();
    readonly List<string> _closed = [];
    readonly DispatcherTimer _saveTimer;
    readonly DispatcherTimer _memoryTimer;
    readonly DispatcherTimer _noteTimer;
    readonly DispatcherTimer _downloadTimer;
    IntPtr _store;
    bool _trimming;
    bool _ready;
    bool _booting;
    WorkspaceVm? _current;
    bool _switching;
    int _tabGuard;
    bool _sidebarOpen = true;
    string _dock = "left";
    Point _brandStart;
    bool _brandDown;
    bool _brandDrag;
    bool _videoFullscreen;
    bool _closing;
    WindowState _beforeFullscreen;
    bool _hasNormal;
    double _nL, _nT, _nW, _nH;
    string? _pendingHost;
    string? _pendingUser;
    string? _pendingPass;
    bool _suggestOpen;
    bool _pickingSuggest;
    int _suggestIndex = -1;
    List<SuggestRow> _suggestions = [];

    public MainWindow() : this(false) { }

    public MainWindow(bool isPrivate)
    {
        App.Log("main ctor");
        _private = isPrivate;
        InitializeComponent();
        App.Log("xaml loaded");
        _state = Store.Current.State;
        _vault = new Vault(Store.Current.Root);
        _bookmarks = new ObservableCollection<Bookmark>(_state.Bookmarks);
        BookmarkList.ItemsSource = _bookmarks;
        WorkspaceList.ItemsSource = _workspaces;
        DownloadList.ItemsSource = _downloads;
        foreach (var item in _state.Downloads)
        {
            _downloads.Add(new DownloadVm
            {
                Name = item.Name,
                Path = item.Path,
                Detail = "Saved",
                Running = false,
                Indeterminate = false,
                Progress = 100
            });
        }

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => Persist();
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _memoryTimer.Tick += async (_, _) =>
        {
            if (_trimming) return;
            _trimming = true;
            try { await TrimMemoryAsync(); }
            finally { _trimming = false; }
        };
        _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _noteTimer.Tick += (_, _) =>
        {
            _noteTimer.Stop();
            StatusNote.IsVisible = false;
        };
        _downloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _downloadTimer.Tick += (_, _) => PumpDownloads();

        if (_private) PrivateBadge.IsVisible = true;
        Cove.Theme.Apply(_state.Accent);
        Cove.Theme.ApplyAppearance(_state.Appearance);
        RamSlider.Value = Math.Clamp(_state.RamCapMb, 1024, 16384);
        RamSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || !_ready) return;
            _state.RamCapMb = (int)RamSlider.Value;
            ScheduleSave();
        };
        WireSettings();
        _dock = _state.SidebarDock is "right" or "bottom" ? _state.SidebarDock : "left";
        if (_state.SidebarSize < 120) _state.SidebarSize = 188;
        SetSidebar(_state.SidebarOpen);
        ShowPanel("workspaces");
        RefreshHistory();
        AddHandler(KeyDownEvent, OnPreviewKey, RoutingStrategies.Tunnel);
        TabList.AddHandler(PointerPressedEvent, TabList_PointerPressed, RoutingStrategies.Tunnel);
        PositionChanged += (_, _) => CaptureNormal();
        Loaded += async (_, _) =>
        {
            App.Log("loaded");
            await BootAsync();
        };
    }

    void WireSettings()
    {
        foreach (var item in AppearanceBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, _state.Appearance, StringComparison.OrdinalIgnoreCase))
            {
                AppearanceBox.SelectedItem = item;
                break;
            }
        }
        if (AppearanceBox.SelectedItem is null && AppearanceBox.Items.Count > 0)
            AppearanceBox.SelectedIndex = 0;
        AccentBox.Items.Clear();
        foreach (var hex in Cove.Theme.Swatches)
        {
            var item = new ComboBoxItem { Content = hex, Tag = hex };
            AccentBox.Items.Add(item);
            if (string.Equals(hex, Cove.Theme.AccentHex, StringComparison.OrdinalIgnoreCase))
                AccentBox.SelectedItem = item;
        }
        if (AccentBox.SelectedItem is null && AccentBox.Items.Count > 0) AccentBox.SelectedIndex = 0;
        AutocompleteCheck.IsChecked = _state.AddressAutocomplete;
        RestoreSessionCheck.IsChecked = _state.RestoreSession;
        AdBlockCheck.IsChecked = _state.AdBlockEnabled;
        AppearanceBox.SelectionChanged += (_, _) =>
        {
            if (!_ready || AppearanceBox.SelectedItem is not ComboBoxItem item) return;
            _state.Appearance = item.Tag as string ?? "dark";
            Cove.Theme.ApplyAppearance(_state.Appearance);
            ScheduleSave();
        };
        AccentBox.SelectionChanged += (_, _) =>
        {
            if (!_ready || AccentBox.SelectedItem is not ComboBoxItem item) return;
            var hex = item.Tag as string ?? Cove.Theme.AccentHex;
            Cove.Theme.Apply(hex);
            _state.Accent = Cove.Theme.AccentHex;
            BrandButton.Foreground = Cove.Theme.Accent;
            UpdateChrome();
            ScheduleSave();
        };
        AutocompleteCheck.IsCheckedChanged += (_, _) =>
        {
            if (!_ready) return;
            _state.AddressAutocomplete = AutocompleteCheck.IsChecked == true;
            if (!_state.AddressAutocomplete) HideSuggestions();
            ScheduleSave();
        };
        RestoreSessionCheck.IsCheckedChanged += (_, _) =>
        {
            if (!_ready) return;
            _state.RestoreSession = RestoreSessionCheck.IsChecked == true;
            ScheduleSave();
        };
        AdBlockCheck.IsCheckedChanged += async (_, _) =>
        {
            if (!_ready) return;
            _state.AdBlockEnabled = AdBlockCheck.IsChecked == true;
            await WebEngine.ApplyBlocking(_state.AdBlockEnabled, _state.AdBlockAllow);
            var tab = ActiveTab();
            if (tab is not null) await SetAdsFlagAsync(tab);
            UpdateShield();
            ScheduleSave();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyBounds();
    }

    async Task BootAsync()
    {
        if (_booting) return;
        _booting = true;
        try
        {
            BootText.Text = "Starting Cove…";
            BootButton.IsVisible = false;
            await WebEngine.Start(_state.AdBlockEnabled, _state.AdBlockAllow, _state.ExtensionPaths);
            if (_closing) return;
            if (_private) _store = WebEngine.CreatePrivateStore();
            if (_private || !_state.RestoreSession || _state.Workspaces.Count == 0)
            {
                var fresh = new WorkspaceVm("Main", string.IsNullOrWhiteSpace(_state.Accent) ? Cove.Theme.AccentHex : _state.Accent);
                _workspaces.Add(fresh);
                _current = fresh;
                fresh.IsActive = true;
                TabList.ItemsSource = fresh.Tabs;
                await CreateTabAsync(fresh, NewTabUrl(), select: true);
            }
            else
            {
                foreach (var saved in _state.Workspaces)
                {
                    var ws = new WorkspaceVm(string.IsNullOrWhiteSpace(saved.Name) ? "Main" : saved.Name, saved.Color) { Id = saved.Id };
                    ws.ActiveIndex = saved.ActiveIndex;
                    _workspaces.Add(ws);
                    var tabs = saved.Tabs.Count == 0 ? [new TabState()] : saved.Tabs;
                    foreach (var tab in tabs)
                    {
                        var url = string.IsNullOrWhiteSpace(tab.Url) ? NewTabUrl() : Normalize(tab.Url);
                        var created = await CreateTabAsync(ws, url, select: false);
                        created.Seen = tab.Seen;
                        if (!string.IsNullOrWhiteSpace(tab.Title)) created.Title = tab.Title;
                    }
                    if (ws.Tabs.Any(t => t.Seen != default))
                    {
                        var newest = ws.Tabs.Select((t, i) => (t, i)).OrderByDescending(x => x.t.Seen).First();
                        ws.ActiveIndex = newest.i;
                    }
                }
                var active = _workspaces.FirstOrDefault(w => w.Id == _state.ActiveWorkspaceId) ?? _workspaces[0];
                SwitchWorkspace(active);
            }
            _ready = true;
            _memoryTimer.Start();
            RefreshExtensions();
            App.Log("session ready");
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            BootText.Text = ex.Message;
            BootButton.IsVisible = true;
        }
        finally
        {
            _booting = false;
        }
    }

    async Task<TabVm> CreateTabAsync(WorkspaceVm ws, string url, bool select, int insertAt = -1)
    {
        var page = WebEngine.OpenPage(_private, _store);
        var tab = new TabVm(page) { Title = "New tab" };
        var index = insertAt;
        if (index < 0)
        {
            var current = ReferenceEquals(ws, _current) ? ActiveTab() : null;
            index = current is null ? ws.Tabs.Count : ws.Tabs.IndexOf(current) + 1;
        }
        if (index < 0 || index > ws.Tabs.Count) index = ws.Tabs.Count;
        ws.Tabs.Insert(index, tab);
        WebSurface.Adopt(page);
        Wire(tab);
        if (select)
        {
            _current = ws;
            TabList.ItemsSource = ws.Tabs;
            ShowTab(tab);
        }
        else page.SetVisible(false);
        page.Navigate(string.IsNullOrWhiteSpace(url) ? NewTabUrl() : url);
        ScheduleSave();
        return tab;
    }

    void Wire(TabVm tab)
    {
        var page = tab.Page;
        page.NavigationStarting += url =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                tab.PageHost = uri.Host;
                tab.Url = uri.ToString();
            }
            tab.Blocked = 0;
            tab.IsSleeping = false;
            if (IsCurrent(tab))
            {
                Progress.IsVisible = true;
                UpdateChrome();
            }
        };
        page.NavigationCompleted += (url, success) =>
        {
            if (!success) App.Log("page failed " + url);
            if (!string.IsNullOrWhiteSpace(url)) tab.Url = url;
            if (IsCurrent(tab)) Progress.IsVisible = false;
            if (success) RememberHistory(tab);
            _ = SetAdsFlagAsync(tab);
            _ = LoadFaviconAsync(tab);
            if (IsCurrent(tab)) UpdateChrome();
        };
        page.TitleChanged += title =>
        {
            tab.Title = string.IsNullOrWhiteSpace(title) ? "New tab" : title;
            if (IsCurrent(tab)) Title = DisplayTitle(tab);
        };
        page.ScriptMessage += (json, source, main) => OnMessage(tab, json, source, main);
        page.PopupRequested += destination =>
        {
            if (_current is null) return;
            _ = CreateTabAsync(_current, string.IsNullOrWhiteSpace(destination) ? NewTabUrl() : destination, select: true);
        };
        page.DownloadReady += offer => OnDownload(offer);
        page.DownloadFinished += (id, status) => FinishDownload(id, status);
        page.Crashed += () =>
        {
            try { page.Reload(); } catch { /* The view may already be closing. */ }
        };
    }

    void OnMessage(TabVm tab, string json, string? source, bool mainFrame)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("kind", out var kindEl)) return;
            var kind = kindEl.GetString();
            if (kind is not null && kind.StartsWith("vault-", StringComparison.Ordinal))
            {
                HandleVault(tab, kind, doc.RootElement, source);
                return;
            }
            if (kind == "here")
            {
                var url = doc.RootElement.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var here) && here.Scheme is "https" or "http" or "cove")
                {
                    tab.Url = here.ToString();
                    tab.PageHost = here.Host;
                }
                var title = doc.RootElement.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title)) tab.Title = title;
                if (IsCurrent(tab))
                {
                    UpdateChrome();
                    Title = DisplayTitle(tab);
                }
                if (ReferenceEquals(ActiveTab(), tab)) ScheduleSave();
                return;
            }
            if (kind == "popout")
            {
                if (IsCurrent(tab)) _ = PopoutAsync(tab);
                return;
            }
            if (kind == "fullscreen")
            {
                if (!IsCurrent(tab) || !mainFrame) return;
                var on = doc.RootElement.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                SetVideoFullscreen(on);
                return;
            }
            if (kind == "search")
            {
                if (source is null || !source.Contains("cove.local", StringComparison.OrdinalIgnoreCase)) return;
                var query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                if (IsCurrent(tab)) NavigateActive(query);
                else tab.Page.Navigate(Resolve(query));
                return;
            }
            if (kind != "login" || _private) return;
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme is not ("https" or "http")) return;
            if (_state.NeverSave.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return;
            var pass = doc.RootElement.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
            if (pass.Length == 0) return;
            _pendingHost = uri.Host;
            _pendingUser = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            _pendingPass = pass;
            SaveText.Text = string.IsNullOrEmpty(_pendingUser)
                ? $"Save password for {uri.Host}?"
                : $"Save password for {_pendingUser} on {uri.Host}?";
            SaveBar.IsVisible = true;
        }
    }

    void OnDownload(DownloadOffer offer)
    {
        var suggested = Path.GetFileName(offer.Name);
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "download";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, suggested);
        offer.Path = path;
        var item = new DownloadVm
        {
            Name = Path.GetFileName(path),
            Path = path,
            Detail = "Starting…",
            Running = true,
            DownloadId = offer.Id,
            Total = offer.Total,
            CancelDownload = () =>
            {
                foreach (var page in AllTabs().Select(t => t.Page))
                    page.CancelDownload(offer.Id);
            }
        };
        _downloads.Insert(0, item);
        _downloadTimer.Start();
        Note($"Downloading {item.Name}");
    }

    void FinishDownload(int id, int status)
    {
        var item = _downloads.FirstOrDefault(d => d.DownloadId == id && d.Running && !d.Settled);
        if (item is null) return;
        item.Settled = true;
        item.Running = false;
        item.Indeterminate = false;
        if (status == 1)
        {
            item.Progress = 100;
            item.Detail = "Saved";
            if (!_private)
            {
                _state.Downloads.Insert(0, new DownloadRecord { Name = item.Name, Path = item.Path, When = DateTimeOffset.Now });
                if (_state.Downloads.Count > 40) _state.Downloads.RemoveAt(_state.Downloads.Count - 1);
                Store.Current.Save();
            }
        }
        else item.Detail = "Stopped";
    }

    void PumpDownloads()
    {
        var any = false;
        foreach (var item in _downloads)
        {
            if (!item.Running) continue;
            any = true;
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path)) continue;
            long len;
            try { len = new FileInfo(item.Path).Length; }
            catch { continue; }
            if (item.Total > 0)
            {
                item.Indeterminate = false;
                item.Progress = Math.Clamp(len * 100d / item.Total, 0, 100);
                item.Detail = $"{FormatBytes(len)} of {FormatBytes(item.Total)}";
            }
            else
            {
                item.Indeterminate = true;
                item.Detail = FormatBytes(len);
            }
        }
        if (!any) _downloadTimer.Stop();
    }

    void ShowTab(TabVm tab)
    {
        var ws = WorkspaceOf(tab);
        if (ws is null) return;
        _current = ws;
        foreach (var item in _workspaces) item.IsActive = ReferenceEquals(item, ws);
        if (tab.Page.IsAsleep)
        {
            tab.Page.Wake();
            tab.IsSleeping = false;
        }
        WebSurface.Activate(tab.Page);
        tab.LastActive = Environment.TickCount64;
        tab.Seen = DateTimeOffset.Now;
        ws.ActiveIndex = Math.Max(0, ws.Tabs.IndexOf(tab));
        _switching = true;
        WorkspaceList.SelectedItem = ws;
        if (!ReferenceEquals(TabList.ItemsSource, ws.Tabs)) TabList.ItemsSource = ws.Tabs;
        TabList.SelectedItem = tab;
        _switching = false;
        BootPanel.IsVisible = false;
        UpdateChrome();
        if (_ready) Persist();
        _tabGuard++;
        Dispatcher.UIThread.Post(() =>
        {
            KeepTab(tab);
            _tabGuard = Math.Max(0, _tabGuard - 1);
        }, DispatcherPriority.Background);
    }

    void KeepTab(TabVm tab)
    {
        var ws = WorkspaceOf(tab);
        if (ws is null || !ReferenceEquals(_current, ws) || !ws.Tabs.Contains(tab)) return;
        _switching = true;
        if (!ReferenceEquals(TabList.ItemsSource, ws.Tabs)) TabList.ItemsSource = ws.Tabs;
        TabList.SelectedItem = tab;
        WebSurface.Activate(tab.Page);
        ws.ActiveIndex = Math.Max(0, ws.Tabs.IndexOf(tab));
        _switching = false;
    }

    void SwitchWorkspace(WorkspaceVm ws)
    {
        _current = ws;
        foreach (var item in _workspaces) item.IsActive = ReferenceEquals(item, ws);
        _switching = true;
        WorkspaceList.SelectedItem = ws;
        if (ws.Tabs.Count == 0)
        {
            _switching = false;
            _ = CreateTabAsync(ws, NewTabUrl(), select: true);
            return;
        }
        if (!ReferenceEquals(TabList.ItemsSource, ws.Tabs)) TabList.ItemsSource = ws.Tabs;
        var index = Math.Clamp(ws.ActiveIndex, 0, ws.Tabs.Count - 1);
        ShowTab(ws.Tabs[index]);
        ScheduleSave();
    }

    void NavigateActive(string input)
    {
        var url = Resolve(input);
        var tab = ActiveTab();
        if (tab is null)
        {
            if (_current is not null) _ = CreateTabAsync(_current, url, select: true);
            return;
        }
        if (tab.Page.IsAsleep) tab.Page.Wake();
        tab.Page.Navigate(url);
    }

    void UpdateChrome()
    {
        var tab = ActiveTab();
        BackButton.IsEnabled = tab?.Page.CanGoBack == true;
        ForwardButton.IsEnabled = tab?.Page.CanGoForward == true;
        if (tab is null)
        {
            Title = _private ? "Private — Cove" : "Cove";
            return;
        }
        Title = DisplayTitle(tab);
        if (!AddressBox.IsFocused)
            AddressBox.Text = IsNewTab(tab.Url) ? "" : tab.Url;
        var marked = _state.Bookmarks.Any(b => b.Url == tab.Url);
        StarButton.Foreground = marked ? Cove.Theme.Accent : Cove.Theme.Ink;
        UpdateShield();
    }

    void UpdateShield()
    {
        var tab = ActiveTab();
        var allowed = tab is not null && _state.AdBlockAllow.Contains(tab.PageHost, StringComparer.OrdinalIgnoreCase);
        var on = _state.AdBlockEnabled && !allowed && !string.IsNullOrEmpty(tab?.PageHost);
        ShieldButton.Foreground = on ? Cove.Theme.Accent : Cove.Theme.Muted;
        ShieldButton.SetValue(ToolTip.TipProperty, !_state.AdBlockEnabled
            ? "Blocking is off. Click to turn it on."
            : allowed
                ? $"Allowed on {tab?.PageHost}. Click to block again."
                : "Blocking ads and trackers. Click to allow this site.");
    }

    async Task SetAdsFlagAsync(TabVm tab)
    {
        if (tab.Page.IsAsleep) return;
        var on = _state.AdBlockEnabled && !_state.AdBlockAllow.Contains(tab.PageHost, StringComparer.OrdinalIgnoreCase);
        try { tab.Page.Run($"window.__coveAdsOn = {(on ? "true" : "false")};"); }
        catch { /* The page may have navigated away. */ }
    }

    async Task LoadFaviconAsync(TabVm tab)
    {
        if (tab.Page.IsAsleep || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) return;
        var origin = tab.Url;
        string? href = null;
        try
        {
            var json = await tab.Page.EvalJson("(() => { const link = document.querySelector('link[rel~=\"icon\"], link[rel=\"shortcut icon\"]'); return link && link.href ? link.href : location.origin + '/favicon.ico'; })()");
            if (!string.IsNullOrWhiteSpace(json)) href = JsonSerializer.Deserialize<string>(json);
        }
        catch { return; }
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) return;
        byte[]? bytes = null;
        try
        {
            if (uri.Scheme == "data")
            {
                var comma = href.IndexOf(',');
                if (comma > 0 && href[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
                    bytes = Convert.FromBase64String(href[(comma + 1)..]);
            }
            else if (uri.Scheme is "http" or "https")
            {
                using var response = await Http.GetAsync(uri);
                if (!response.IsSuccessStatusCode) return;
                bytes = await response.Content.ReadAsByteArrayAsync();
            }
        }
        catch { return; }
        if (bytes is null || bytes.Length == 0 || bytes.Length > 1_500_000) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!string.Equals(tab.Url, origin, StringComparison.Ordinal)) return;
            try
            {
                using var stream = new MemoryStream(bytes);
                tab.Favicon = new Bitmap(stream);
            }
            catch { /* Not an image. */ }
        });
    }

    void RememberHistory(TabVm tab)
    {
        if (_private) return;
        if (!Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("https" or "http")) return;
        if (uri.Host.Equals("cove.local", StringComparison.OrdinalIgnoreCase)) return;
        var title = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title;
        if (_state.History.Count > 0 && _state.History[0].Url == tab.Url)
        {
            _state.History[0].Title = title;
            _state.History[0].Visited = DateTimeOffset.Now;
        }
        else
        {
            _state.History.Insert(0, new HistoryEntry { Title = title, Url = tab.Url, Visited = DateTimeOffset.Now });
            if (_state.History.Count > 1500) _state.History.RemoveAt(_state.History.Count - 1);
        }
        RefreshHistory();
        ScheduleSave();
    }

    void RefreshHistory()
    {
        var filter = HistorySearch.Text?.Trim() ?? "";
        IEnumerable<HistoryEntry> rows = _state.History;
        if (filter.Length > 0)
        {
            rows = rows.Where(h =>
                h.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                h.Url.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        HistoryList.ItemsSource = rows.Take(300).Select(h => new HistoryRow
        {
            Title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title,
            Url = h.Url,
            When = When(h.Visited)
        }).ToList();
    }

    void CloseTab(TabVm tab)
    {
        var ws = WorkspaceOf(tab);
        if (ws is null) return;
        if (IsWeb(tab.Url))
        {
            _closed.Add(tab.Url);
            if (_closed.Count > 20) _closed.RemoveAt(0);
        }
        var index = ws.Tabs.IndexOf(tab);
        ws.Tabs.Remove(tab);
        WebSurface.Drop(tab.Page);
        tab.Page.Dispose();
        if (ws.Tabs.Count == 0)
        {
            if (_private && _workspaces.Count == 1)
            {
                Close();
                return;
            }
            _ = CreateTabAsync(ws, NewTabUrl(), select: ReferenceEquals(ws, _current));
            return;
        }
        if (ReferenceEquals(ws, _current))
            ShowTab(ws.Tabs[Math.Clamp(index, 0, ws.Tabs.Count - 1)]);
        ScheduleSave();
    }

    bool HandleShortcut(Key key, bool accel, bool shift, bool alt)
    {
        if (accel && shift && key == Key.N)
        {
            new MainWindow(isPrivate: true).Show();
            return true;
        }
        if (accel && shift && key == Key.T)
        {
            if (_closed.Count == 0 || _current is null) return true;
            var url = _closed[^1];
            _closed.RemoveAt(_closed.Count - 1);
            _ = CreateTabAsync(_current, url, select: true);
            return true;
        }
        if (accel && !shift && key == Key.T)
        {
            if (_current is not null) _ = CreateTabAsync(_current, NewTabUrl(), select: true);
            return true;
        }
        if (accel && !shift && key == Key.W)
        {
            var tab = ActiveTab();
            if (tab is not null) CloseTab(tab);
            return true;
        }
        if (accel && key == Key.L)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            return true;
        }
        if (accel && key == Key.D)
        {
            ToggleBookmark();
            return true;
        }
        if (accel && key == Key.Tab && _current is { Tabs.Count: > 1 })
        {
            var tabs = _current.Tabs;
            var index = Math.Max(0, tabs.IndexOf(ActiveTab()!));
            var next = shift ? (index - 1 + tabs.Count) % tabs.Count : (index + 1) % tabs.Count;
            ShowTab(tabs[next]);
            return true;
        }
        if ((accel && key == Key.R) || key == Key.F5)
        {
            ActiveTab()?.Page.Reload();
            return true;
        }
        if (alt && key == Key.Left)
        {
            ActiveTab()?.Page.GoBack();
            return true;
        }
        if (alt && key == Key.Right)
        {
            ActiveTab()?.Page.GoForward();
            return true;
        }
        if (key == Key.Escape && !accel && !alt && Progress.IsVisible)
        {
            ActiveTab()?.Page.Stop();
            Progress.IsVisible = false;
            return true;
        }
        return false;
    }

    void ToggleBookmark()
    {
        var tab = ActiveTab();
        if (tab is null || !IsWeb(tab.Url))
        {
            Note("Nothing to bookmark on this page.");
            return;
        }
        var existing = _state.Bookmarks.FirstOrDefault(b => b.Url == tab.Url);
        if (existing is null)
        {
            var mark = new Bookmark { Title = tab.Title, Url = tab.Url };
            _state.Bookmarks.Insert(0, mark);
            _bookmarks.Insert(0, mark);
            Note("Bookmarked.");
        }
        else
        {
            _state.Bookmarks.Remove(existing);
            _bookmarks.Remove(existing);
            Note("Bookmark removed.");
        }
        UpdateChrome();
        Store.Current.Save();
    }

    async Task PopoutAsync(TabVm tab)
    {
        string? json;
        try { json = await tab.Page.EvalJson(WebScripts.Popout); }
        catch
        {
            Note("Couldn't pop that video out.");
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                Note("No video on this page.");
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var value = doc.RootElement;
                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty("__coveError", out _))
                {
                    Note("Couldn't pop that video out.");
                    return;
                }
                var ok = value.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                var mode = value.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : "";
                if (!ok)
                {
                    var reason = value.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : "";
                    Note(reason == "none" ? "No video on this page." : "This site won't let the video leave the page.");
                    return;
                }
                if (mode == "src" && value.TryGetProperty("src", out var srcEl) && srcEl.GetString() is string src)
                {
                    var time = value.TryGetProperty("time", out var timeEl) && timeEl.TryGetDouble(out var seconds) ? seconds : 0;
                    new VideoWindow(src, time).Show();
                    Note("Playing in a floating window.");
                    return;
                }
                Note(mode == "exit" ? "Video returned to the page." : "Video popped out.");
            }
            catch
            {
                Note("Couldn't pop that video out.");
            }
        });
    }

    async Task TrimMemoryAsync()
    {
        long bytes;
        try { bytes = MemoryUsage.WorkingSet(); }
        catch { return; }
        var cap = (long)_state.RamCapMb * 1024L * 1024L;
        RamLabel.Text = $"{FormatBytes(bytes)} in use · cap {FormatBytes(cap)}";
        UpdateUsage();
        if (bytes <= cap) return;
        var active = ActiveTab();
        foreach (var tab in AllTabs().OrderBy(t => t.LastActive).ToList())
        {
            if (ReferenceEquals(tab, active) || tab.Page.IsAsleep) continue;
            bool playing;
            try { playing = await tab.Page.IsPlayingAudioAsync().WaitAsync(TimeSpan.FromMilliseconds(500)); }
            catch { playing = false; }
            if (playing || _closing) continue;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                tab.Page.Sleep();
                tab.IsSleeping = true;
            });
            if (MemoryUsage.WorkingSet() <= cap) break;
        }
        await Dispatcher.UIThread.InvokeAsync(UpdateUsage);
    }

    void UpdateUsage()
    {
        var rows = new List<MemoryRow>();
        foreach (var tab in AllTabs())
        {
            rows.Add(new MemoryRow
            {
                Kind = "Tab",
                Name = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title,
                Amount = tab.Page.IsAsleep ? "Asleep" : "Awake"
            });
        }
        foreach (var ext in WebEngine.ExtensionPacks)
            rows.Add(new MemoryRow { Kind = "Extension", Name = ext.Name, Amount = ext.HasScripts ? "In page" : "No scripts" });
        MemoryList.ItemsSource = rows;
    }

    void RefreshExtensions()
    {
        ExtensionHost.Children.Clear();
        var packs = WebEngine.ExtensionPacks;
        if (packs.Count == 0)
        {
            ExtensionHost.Children.Add(new TextBlock { Text = "No extensions loaded.", Foreground = Cove.Theme.Muted, Margin = new Thickness(0, 0, 0, 8) });
            return;
        }
        foreach (var ext in packs)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            var remove = new Button { Content = "Remove", Tag = ext.Path };
            remove.Click += (_, _) =>
            {
                if (remove.Tag is not string path) return;
                _state.ExtensionPaths.RemoveAll(item => item == path);
                WebEngine.SetExtensions(_state.ExtensionPaths);
                Store.Current.Save();
                RefreshExtensions();
                Note("Reload open tabs to drop that extension.");
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(new TextBlock { Text = ext.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(remove);
            ExtensionHost.Children.Add(row);
        }
    }

    void Snapshot()
    {
        if (_private) return;
        _state.Workspaces = _workspaces.Select(ws => new WorkspaceState
        {
            Id = ws.Id,
            Name = ws.Name,
            Color = ws.ColorHex,
            ActiveIndex = Math.Clamp(ws.ActiveIndex, 0, Math.Max(0, ws.Tabs.Count - 1)),
            Tabs = ws.Tabs.Select(t => new TabState { Url = Collapse(t.Url), Title = t.Title, Seen = t.Seen }).ToList()
        }).ToList();
        _state.ActiveWorkspaceId = _current?.Id;
        _state.SidebarOpen = _sidebarOpen;
        _state.SidebarDock = _dock;
        RememberSidebarSize();
        _state.Accent = Cove.Theme.AccentHex;
    }

    void Persist()
    {
        _saveTimer.Stop();
        if (_private) return;
        Snapshot();
        SaveBounds();
        Store.Current.Save();
    }

    void ScheduleSave()
    {
        if (_private || !_ready) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void ApplyBounds()
    {
        if (double.IsNaN(_state.Left) || _state.Width < 800 || _state.Height < 500)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            var scale = Scale();
            var left = _state.Left * scale;
            var top = _state.Top * scale;
            var right = left + _state.Width * scale;
            var bottom = top + _state.Height * scale;
            var onScreen = Screens?.All.Any(screen =>
                right > screen.Bounds.X + 80 &&
                left < screen.Bounds.Right - 80 &&
                bottom > screen.Bounds.Y + 80 &&
                top < screen.Bounds.Bottom - 80) == true;
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint((int)left, (int)top);
                Width = _state.Width;
                Height = _state.Height;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_state.Maximized) WindowState = WindowState.Maximized;
        CaptureNormal();
    }

    void SaveBounds()
    {
        CaptureNormal();
        _state.Maximized = WindowState == WindowState.Maximized;
        if (_hasNormal)
        {
            _state.Left = _nL;
            _state.Top = _nT;
            _state.Width = _nW;
            _state.Height = _nH;
        }
    }

    void CaptureNormal()
    {
        if (WindowState != WindowState.Normal || _videoFullscreen) return;
        var scale = Scale();
        _nL = Position.X / scale;
        _nT = Position.Y / scale;
        _nW = Width;
        _nH = Height;
        _hasNormal = _nW >= 800 && _nH >= 500;
    }

    double Scale() => (Screens?.ScreenFromWindow(this) ?? Screens?.Primary)?.Scaling ?? 1;

    void SetSidebar(bool open)
    {
        _sidebarOpen = open;
        _state.SidebarOpen = open;
        ApplyChrome();
        HighlightRail(CurrentPanel());
    }

    void ApplyChrome()
    {
        if (_videoFullscreen) return;
        _dock = _dock is "right" or "bottom" ? _dock : "left";
        const double rail = 36;
        var openSize = Math.Clamp(_state.SidebarSize, _dock == "bottom" ? 120 : 150, _dock == "bottom" ? 420 : 520);
        var size = _sidebarOpen ? openSize : rail;
        var star = new GridLength(1, GridUnitType.Star);
        Dock.ColumnDefinitions.Clear();
        Dock.RowDefinitions.Clear();

        if (_dock == "bottom")
        {
            Dock.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Dock.RowDefinitions.Add(new RowDefinition { Height = star });
            Dock.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Dock.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(size),
                MinHeight = _sidebarOpen ? 110 : rail,
                MaxHeight = _sidebarOpen ? 480 : rail
            });
            Place(Browser, 0, 0);
            Place(SidebarSplitter, 0, 1);
            Place(SidebarShell, 0, 2);
            SidebarSplitter.Width = double.NaN;
            SidebarSplitter.Height = 5;
            SidebarSplitter.ResizeDirection = GridResizeDirection.Rows;
            SidebarSplitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            LayoutSidebar(horizontal: true);
        }
        else if (_dock == "right")
        {
            Dock.RowDefinitions.Add(new RowDefinition { Height = star });
            Dock.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Dock.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(size),
                MinWidth = _sidebarOpen ? 150 : rail,
                MaxWidth = _sidebarOpen ? 560 : rail
            });
            Place(Browser, 0, 0);
            Place(SidebarSplitter, 1, 0);
            Place(SidebarShell, 2, 0);
            SidebarSplitter.Height = double.NaN;
            SidebarSplitter.Width = 5;
            SidebarSplitter.ResizeDirection = GridResizeDirection.Columns;
            SidebarSplitter.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            LayoutSidebar(horizontal: false);
        }
        else
        {
            Dock.RowDefinitions.Add(new RowDefinition { Height = star });
            Dock.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(size),
                MinWidth = _sidebarOpen ? 150 : rail,
                MaxWidth = _sidebarOpen ? 560 : rail
            });
            Dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Dock.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Place(SidebarShell, 0, 0);
            Place(SidebarSplitter, 1, 0);
            Place(Browser, 2, 0);
            SidebarSplitter.Height = double.NaN;
            SidebarSplitter.Width = 5;
            SidebarSplitter.ResizeDirection = GridResizeDirection.Columns;
            SidebarSplitter.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            LayoutSidebar(horizontal: false);
        }

        SidebarSplitter.IsVisible = _sidebarOpen;
        PanelHost.IsVisible = _sidebarOpen;
        SidebarShell.IsVisible = true;
    }

    static void Place(Control element, int column, int row)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        Grid.SetColumnSpan(element, 1);
        Grid.SetRowSpan(element, 1);
    }

    void LayoutSidebar(bool horizontal)
    {
        SidebarGrid.RowDefinitions.Clear();
        SidebarGrid.ColumnDefinitions.Clear();
        if (horizontal)
        {
            SidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            SidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            SidebarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Rail.Orientation = Orientation.Horizontal;
            Grid.SetRow(Rail, 0);
            Grid.SetColumn(Rail, 0);
            Grid.SetRow(PanelHost, 1);
            Grid.SetColumn(PanelHost, 0);
            SidebarShell.BorderThickness = new Thickness(0, 1, 0, 0);
            PanelHost.Margin = new Thickness(8, 0, 8, 6);
            BrandButton.Height = 36;
        }
        else
        {
            SidebarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            SidebarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Rail.Orientation = Orientation.Vertical;
            Grid.SetRow(Rail, 0);
            Grid.SetColumn(Rail, 0);
            Grid.SetRow(PanelHost, 0);
            Grid.SetColumn(PanelHost, 1);
            SidebarShell.BorderThickness = _dock == "right" ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            PanelHost.Margin = new Thickness(0, 4, 6, 6);
            BrandButton.Height = 40;
        }
    }

    void RememberSidebarSize()
    {
        if (!_sidebarOpen || _videoFullscreen) return;
        var size = _dock switch
        {
            "bottom" when Dock.RowDefinitions.Count > 2 => Dock.RowDefinitions[2].ActualHeight,
            "right" when Dock.ColumnDefinitions.Count > 2 => Dock.ColumnDefinitions[2].ActualWidth,
            "left" when Dock.ColumnDefinitions.Count > 0 => Dock.ColumnDefinitions[0].ActualWidth,
            _ => 0
        };
        if (size >= 110) _state.SidebarSize = size;
    }

    void SidebarSplitter_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        RememberSidebarSize();
        ScheduleSave();
    }

    void Brand_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(BrandButton).Properties.IsLeftButtonPressed) return;
        _brandStart = e.GetPosition(this);
        _brandDown = true;
        _brandDrag = false;
        e.Pointer.Capture(BrandButton);
        e.Handled = true;
    }

    void Brand_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_brandDown) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _brandStart.X;
        var dy = pos.Y - _brandStart.Y;
        if (dx * dx + dy * dy > 100) _brandDrag = true;
        if (!_brandDrag) return;
        var next = DockFor(pos);
        if (next == _dock) return;
        RememberSidebarSize();
        _dock = next;
        ApplyChrome();
        Note(next switch
        {
            "right" => "Sidebar on the right",
            "bottom" => "Sidebar on the bottom",
            _ => "Sidebar on the left"
        });
    }

    void Brand_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_brandDown) return;
        _brandDown = false;
        e.Pointer.Capture(null);
        if (_brandDrag)
        {
            _state.SidebarDock = _dock;
            ScheduleSave();
        }
        else
        {
            SetSidebar(!_sidebarOpen);
            ScheduleSave();
        }
        _brandDrag = false;
        e.Handled = true;
    }

    string DockFor(Point pos)
    {
        if (pos.Y > Bounds.Height * 0.68) return "bottom";
        if (pos.X < Bounds.Width * 0.28) return "left";
        if (pos.X > Bounds.Width * 0.72) return "right";
        return _dock;
    }

    void ShowPanel(string name)
    {
        PanelWorkspaces.IsVisible = name == "workspaces";
        PanelBookmarks.IsVisible = name == "bookmarks";
        PanelHistory.IsVisible = name == "history";
        PanelDownloads.IsVisible = name == "downloads";
        PanelExtensions.IsVisible = name == "extensions";
        PanelLimiter.IsVisible = name == "limiter";
        PanelSettings.IsVisible = name == "settings";
        HighlightRail(name);
    }

    void HighlightRail(string name)
    {
        foreach (var button in new[] { RailWorkspaces, RailBookmarks, RailHistory, RailDownloads, RailExtensions, RailLimiter, RailSettings })
        {
            var on = button.Tag as string == name && _sidebarOpen;
            button.Foreground = on ? Cove.Theme.Accent : Cove.Theme.Ink;
        }
    }

    string CurrentPanel()
    {
        if (PanelBookmarks.IsVisible) return "bookmarks";
        if (PanelHistory.IsVisible) return "history";
        if (PanelDownloads.IsVisible) return "downloads";
        if (PanelExtensions.IsVisible) return "extensions";
        if (PanelLimiter.IsVisible) return "limiter";
        if (PanelSettings.IsVisible) return "settings";
        return "workspaces";
    }

    void Note(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Note(text));
            return;
        }
        StatusNote.Text = text;
        StatusNote.IsVisible = true;
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    void ClearPending()
    {
        _pendingHost = _pendingUser = _pendingPass = null;
        SaveBar.IsVisible = false;
    }

    TabVm? ActiveTab() => TabList.SelectedItem as TabVm ?? (_current is { Tabs.Count: > 0 } ws ? ws.Tabs[Math.Clamp(ws.ActiveIndex, 0, ws.Tabs.Count - 1)] : null);
    bool IsCurrent(TabVm tab) => ReferenceEquals(tab, ActiveTab());
    WorkspaceVm? WorkspaceOf(TabVm tab) => _workspaces.FirstOrDefault(w => w.Tabs.Contains(tab));
    IEnumerable<TabVm> AllTabs() => _workspaces.SelectMany(w => w.Tabs);

    string DisplayTitle(TabVm tab)
    {
        var prefix = _private ? "Private — " : "";
        if (IsNewTab(tab.Url) || tab.Title is "New tab" or "Cove") return _private ? "Private — Cove" : "Cove";
        return prefix + tab.Title + " — Cove";
    }

    string NewTabUrl() => "cove://cove.local/newtab.html?accent=" + Cove.Theme.AccentHex.TrimStart('#');

    static string Collapse(string url) => IsNewTab(url) ? "cove://newtab" : url;

    string Normalize(string url)
    {
        if (url.Contains("passwords", StringComparison.OrdinalIgnoreCase) && url.Contains("cove.local", StringComparison.OrdinalIgnoreCase))
            return "cove://cove.local/passwords.html";
        if (IsNewTab(url) || url.Contains("://cove.local/", StringComparison.OrdinalIgnoreCase)) return NewTabUrl();
        return url;
    }

    static bool IsNewTab(string url) =>
        url.Contains("cove.local/newtab", StringComparison.OrdinalIgnoreCase) || url.Equals("cove://newtab", StringComparison.OrdinalIgnoreCase);

    static bool IsWeb(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && !uri.Host.Equals("cove.local", StringComparison.OrdinalIgnoreCase);

    string Resolve(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return NewTabUrl();
        if (input.StartsWith("cove://newtab", StringComparison.OrdinalIgnoreCase)) return NewTabUrl();
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http") return absolute.ToString();
        var host = input.Split('/')[0];
        var looksLikeHost = !input.Contains(' ') && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) || host.Contains('.'));
        if (looksLikeHost) return "https://" + input;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        if (local.Date == DateTime.Today) return local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return local.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    static string FormatBytes(double bytes)
    {
        var gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.0} GB";
        return $"{bytes / 1024d / 1024d:0} MB";
    }

    static string UniquePath(string folder, string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return path;
    }

    static T? Ancestor<T>(StyledElement? node) where T : StyledElement
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node.Parent as StyledElement;
        }
        return null;
    }

    void SetVideoFullscreen(bool on)
    {
        if (_videoFullscreen == on) return;
        _videoFullscreen = on;
        if (on)
        {
            _beforeFullscreen = WindowState;
            CaptureNormal();
            TabChrome.IsVisible = false;
            Shell.RowDefinitions[0].Height = new GridLength(0);
            AddressChrome.IsVisible = false;
            Browser.RowDefinitions[0].Height = new GridLength(0);
            Browser.RowDefinitions[1].Height = new GridLength(0);
            Progress.IsVisible = false;
            SaveBar.IsVisible = false;
            SidebarShell.IsVisible = false;
            SidebarSplitter.IsVisible = false;
            Dock.ColumnDefinitions.Clear();
            Dock.RowDefinitions.Clear();
            Dock.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Dock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Place(Browser, 0, 0);
            WindowState = WindowState.FullScreen;
        }
        else
        {
            TabChrome.IsVisible = true;
            Shell.RowDefinitions[0].Height = new GridLength(40);
            AddressChrome.IsVisible = true;
            Browser.RowDefinitions[0].Height = new GridLength(40);
            Browser.RowDefinitions[1].Height = new GridLength(2);
            SidebarShell.IsVisible = true;
            ApplyChrome();
            if (_beforeFullscreen == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                if (_hasNormal)
                {
                    var scale = Scale();
                    Position = new PixelPoint((int)(_nL * scale), (int)(_nT * scale));
                    Width = _nW;
                    Height = _nH;
                }
            }
        }
    }

    void OnPreviewKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _videoFullscreen)
        {
            ActiveTab()?.Page.Run("document.exitFullscreen?.()");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && AddressBox.IsFocused)
        {
            ActiveTab()?.Page.Focus();
            e.Handled = true;
            return;
        }
        var mods = e.KeyModifiers;
        var accel = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
        if (HandleShortcut(e.Key, accel, mods.HasFlag(KeyModifiers.Shift), mods.HasFlag(KeyModifiers.Alt)))
            e.Handled = true;
    }

    void Drag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is StyledElement source && (Ancestor<Button>(source) is not null || Ancestor<TextBox>(source) is not null || Ancestor<ListBoxItem>(source) is not null))
            return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }
        try { BeginMoveDrag(e); } catch { /* The window is already moving. */ }
    }

    void Back_Click(object? sender, RoutedEventArgs e) => ActiveTab()?.Page.GoBack();
    void Forward_Click(object? sender, RoutedEventArgs e) => ActiveTab()?.Page.GoForward();
    void Reload_Click(object? sender, RoutedEventArgs e) => ActiveTab()?.Page.Reload();
    void NewTab_Click(object? sender, RoutedEventArgs e) { if (_current is not null) _ = CreateTabAsync(_current, NewTabUrl(), select: true); }
    void Bookmark_Click(object? sender, RoutedEventArgs e) => ToggleBookmark();

    void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is TabVm tab) CloseTab(tab);
        e.Handled = true;
    }

    void TabList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_switching || _tabGuard > 0) return;
        if (TabList.SelectedItem is TabVm tab) ShowTab(tab);
    }

    void TabList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabList).Properties.IsMiddleButtonPressed) return;
        if (e.Source is not StyledElement source) return;
        if (Ancestor<ListBoxItem>(source)?.DataContext is TabVm tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideSuggestions();
            return;
        }
        if (_suggestOpen && _suggestions.Count > 0 && e.Key is Key.Down or Key.Up)
        {
            if (e.Key == Key.Down) _suggestIndex = Math.Min(_suggestions.Count - 1, _suggestIndex + 1);
            else _suggestIndex = Math.Max(0, _suggestIndex <= 0 ? 0 : _suggestIndex - 1);
            _pickingSuggest = true;
            SuggestList.SelectedIndex = _suggestIndex;
            _pickingSuggest = false;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        if (_suggestOpen && _suggestIndex >= 0 && _suggestIndex < _suggestions.Count)
            NavigateActive(_suggestions[_suggestIndex].Url);
        else
            NavigateActive(AddressBox.Text ?? "");
        HideSuggestions();
        ActiveTab()?.Page.Focus();
        e.Handled = true;
    }

    void AddressBox_GotFocus(object? sender, GotFocusEventArgs e)
    {
        AddressBox.SelectAll();
        RefreshSuggestions(AddressBox.Text ?? "");
    }

    void AddressBox_LostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(HideSuggestions, DispatcherPriority.Background);

    void AddressBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!AddressBox.IsFocused) return;
        RefreshSuggestions(AddressBox.Text ?? "");
    }

    void SuggestList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pickingSuggest || SuggestList.SelectedItem is not SuggestRow row) return;
        NavigateActive(row.Url);
        HideSuggestions();
        ActiveTab()?.Page.Focus();
    }

    void RefreshSuggestions(string query)
    {
        if (!_state.AddressAutocomplete || _private)
        {
            HideSuggestions();
            return;
        }
        query = query.Trim();
        if (query.Length == 0)
        {
            HideSuggestions();
            return;
        }
        var matches = new List<SuggestRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string title, string url, string kind)
        {
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url)) return;
            if (!title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !url.Contains(query, StringComparison.OrdinalIgnoreCase)) return;
            matches.Add(new SuggestRow { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url, Kind = kind });
        }
        foreach (var mark in _state.Bookmarks) Add(mark.Title, mark.Url, "Bookmark");
        foreach (var entry in _state.History) Add(entry.Title, entry.Url, "History");
        _suggestions = matches.Take(8).ToList();
        if (_suggestions.Count == 0)
        {
            HideSuggestions();
            return;
        }
        _suggestIndex = -1;
        SuggestList.ItemsSource = _suggestions;
        SuggestPopup.IsVisible = true;
        _suggestOpen = true;
    }

    void HideSuggestions()
    {
        _suggestOpen = false;
        _suggestIndex = -1;
        SuggestPopup.IsVisible = false;
        SuggestList.SelectedItem = null;
    }

    void WorkspaceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        if (WorkspaceList.SelectedItem is WorkspaceVm ws) SwitchWorkspace(ws);
    }

    async void WorkspaceList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (WorkspaceList.SelectedItem is not WorkspaceVm ws) return;
        var name = await Prompts.Ask(this, "Rename workspace", ws.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        ws.Name = name;
        ScheduleSave();
    }

    async void AddWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var name = await Prompts.Ask(this, "New workspace", "Workspace");
        if (string.IsNullOrWhiteSpace(name)) return;
        var color = Cove.Theme.Swatches[_workspaces.Count % Cove.Theme.Swatches.Length];
        var ws = new WorkspaceVm(name, color);
        _workspaces.Add(ws);
        SwitchWorkspace(ws);
    }

    async void DeleteWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not WorkspaceVm ws) return;
        if (_workspaces.Count <= 1)
        {
            Note("Keep at least one workspace.");
            return;
        }
        if (ws.Tabs.Count > 1 && !await Prompts.Confirm(this, "Remove workspace", $"Close {ws.Name} and its tabs?")) return;
        foreach (var tab in ws.Tabs.ToList())
        {
            WebSurface.Drop(tab.Page);
            tab.Page.Dispose();
        }
        ws.Tabs.Clear();
        var index = _workspaces.IndexOf(ws);
        _workspaces.Remove(ws);
        SwitchWorkspace(_workspaces[Math.Clamp(index - 1, 0, _workspaces.Count - 1)]);
        e.Handled = true;
    }

    void CycleColor_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
        if ((sender as Control)?.Tag is not WorkspaceVm ws) return;
        var index = Array.IndexOf(Cove.Theme.Swatches, ws.ColorHex.ToUpperInvariant());
        ws.SetColor(Cove.Theme.Swatches[(index + 1) % Cove.Theme.Swatches.Length]);
        ScheduleSave();
        e.Handled = true;
    }

    void Rail_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string name) return;
        if (_sidebarOpen && CurrentPanel() == name)
        {
            SetSidebar(false);
            ScheduleSave();
            return;
        }
        if (!_sidebarOpen) SetSidebar(true);
        ShowPanel(name);
        ScheduleSave();
    }

    void Bookmark_Open(object? sender, TappedEventArgs e)
    {
        if (BookmarkList.SelectedItem is Bookmark mark) NavigateActive(mark.Url);
    }

    void History_Open(object? sender, TappedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryRow row) NavigateActive(row.Url);
    }

    void HistorySearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshHistory();

    async void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Prompts.Confirm(this, "Clear history", "Remove every page from history on this computer?")) return;
        _state.History.Clear();
        RefreshHistory();
        Store.Current.Save();
    }

    void Download_Open(object? sender, TappedEventArgs e)
    {
        if (e.Source is StyledElement source && Ancestor<Button>(source) is not null) return;
        if (DownloadList.SelectedItem is not DownloadVm item || string.IsNullOrWhiteSpace(item.Path)) return;
        if (!File.Exists(item.Path))
        {
            Note("That file is no longer there.");
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", item.Path },
                UseShellExecute = true
            });
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            ArgumentList = { "-R", item.Path },
            UseShellExecute = false
        });
    }

    void CancelDownload_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is DownloadVm item && item.Running)
        {
            try { item.CancelDownload?.Invoke(); } catch { /* Already finished. */ }
        }
        e.Handled = true;
    }

    async void Shield_Click(object? sender, RoutedEventArgs e)
    {
        if (!_state.AdBlockEnabled)
        {
            _state.AdBlockEnabled = true;
            Note("Blocking is on.");
        }
        else
        {
            var host = ActiveTab()?.PageHost;
            if (string.IsNullOrWhiteSpace(host) || host.Equals("cove.local", StringComparison.OrdinalIgnoreCase)) return;
            var had = _state.AdBlockAllow.RemoveAll(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (had > 0) Note($"Blocking {host} again.");
            else
            {
                _state.AdBlockAllow.Add(host);
                Note($"Allowed {host}.");
            }
        }
        await WebEngine.ApplyBlocking(_state.AdBlockEnabled, _state.AdBlockAllow);
        var tab = ActiveTab();
        if (tab is not null) await SetAdsFlagAsync(tab);
        UpdateShield();
        Store.Current.Save();
    }

    async void Shield_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ShieldButton).Properties.IsRightButtonPressed) return;
        _state.AdBlockEnabled = !_state.AdBlockEnabled;
        Note(_state.AdBlockEnabled ? "Blocking is on." : "Blocking is paused.");
        await WebEngine.ApplyBlocking(_state.AdBlockEnabled, _state.AdBlockAllow);
        var tab = ActiveTab();
        if (tab is not null) await SetAdsFlagAsync(tab);
        UpdateShield();
        Store.Current.Save();
        e.Handled = true;
    }

    void SaveNotNow_Click(object? sender, RoutedEventArgs e) => ClearPending();

    void SaveNever_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_pendingHost) && !_state.NeverSave.Contains(_pendingHost, StringComparer.OrdinalIgnoreCase))
            _state.NeverSave.Add(_pendingHost);
        ClearPending();
        Store.Current.Save();
    }

    async void SavePassword_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingHost) || string.IsNullOrEmpty(_pendingPass)) return;
        if (!await VaultWindow.Ensure(this, _vault)) return;
        _vault.Add(_pendingHost, _pendingUser ?? "", _pendingPass);
        ClearPending();
        foreach (var open in AllTabs())
        {
            if (open.Url.Contains("cove.local/passwords", StringComparison.OrdinalIgnoreCase))
                SendVault(open, null);
        }
        Note(OperatingSystem.IsMacOS() ? "Password saved on this Mac." : "Password saved on this PC.");
    }

    async void Passwords_Click(object? sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        const string url = "cove://cove.local/passwords.html";
        var existing = _current.Tabs.FirstOrDefault(t => t.Url.Contains("cove.local/passwords", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ShowTab(existing);
            return;
        }
        await CreateTabAsync(_current, url, select: true);
    }

    void HandleVault(TabVm tab, string kind, JsonElement root, string? source)
    {
        if (source is null || !source.Contains("cove.local/passwords", StringComparison.OrdinalIgnoreCase)) return;
        _ = HandleVaultAsync(tab, kind, root);
    }

    async Task HandleVaultAsync(TabVm tab, string kind, JsonElement root)
    {
        string? error = null;
        var password = root.TryGetProperty("password", out var passEl) ? passEl.GetString() ?? "" : "";
        var mode = root.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : "cove";
        var login = mode is "windows" or "mac" or "login";
        var machine = OperatingSystem.IsMacOS() ? "Mac" : "PC";
        if (kind == "vault-setup")
        {
            if (_vault.Exists) error = $"A vault already exists on this {machine}.";
            else if (login)
            {
                if (OperatingSystem.IsWindows())
                {
                    _vault.CreateForWindows();
                }
                else if (!MacSignIn.Check(password))
                {
                    error = "That isn't this Mac's sign-in password. Use a Cove password instead.";
                }
                else _vault.CreateForWindows();
            }
            else if (password.Length < 4) error = "Use at least 4 characters.";
            else _vault.Create(password);
        }
        else if (kind == "vault-unlock")
        {
            if (mode == "touchid")
            {
                var (ok, fail) = await _vault.UnlockWithTouchIdAsync();
                if (!ok) error = fail;
            }
            else if (login)
            {
                var ok = OperatingSystem.IsWindows()
                    ? _vault.UnlockWithWindows()
                    : MacSignIn.Check(password) && _vault.UnlockWithWindows();
                if (!ok) error = $"Couldn't unlock with this {machine}'s sign-in.";
            }
            else if (!_vault.Unlock(password))
            {
                error = "That password didn't open the vault.";
            }
        }
        else if (kind == "vault-lock")
        {
            _vault.Lock();
        }
        else if (kind == "vault-delete")
        {
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (_vault.IsUnlocked && !string.IsNullOrEmpty(id)) _vault.Delete(id);
        }
        else if (kind == "vault-windows")
        {
            if (!_vault.IsUnlocked) error = "Unlock the vault first.";
            else if (OperatingSystem.IsWindows())
            {
                _vault.EnableWindowsUnlock();
            }
            else if (!MacSignIn.Check(password)) error = "That isn't this Mac's sign-in password.";
            else _vault.EnableWindowsUnlock();
        }
        SendVault(tab, error);
        if (kind is "vault-lock" or "vault-delete" or "vault-unlock" or "vault-setup" or "vault-windows")
        {
            foreach (var other in AllTabs())
            {
                if (ReferenceEquals(other, tab)) continue;
                if (other.Url.Contains("cove.local/passwords", StringComparison.OrdinalIgnoreCase))
                    SendVault(other, null);
            }
        }
    }

    void SendVault(TabVm tab, string? error)
    {
        var message = new VaultPageState
        {
            Exists = _vault.Exists,
            Unlocked = _vault.IsUnlocked,
            Windows = _vault.HasWindowsUnlock,
            TouchId = _vault.HasTouchId,
            Platform = OperatingSystem.IsMacOS() ? "mac" : "windows",
            Error = error,
            Entries = _vault.IsUnlocked ? _vault.Entries.ToList() : null
        };
        tab.Page.PostJson(JsonSerializer.Serialize(message, WebJson));
    }

    static readonly JsonSerializerOptions WebJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    async void LoadExtension_Click(object? sender, RoutedEventArgs e)
    {
        if (_private)
        {
            Note("Load extensions from the main window.");
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder containing manifest.json",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            Note("Cove couldn't read that folder.");
            return;
        }
        if (ExtensionPack.Read(path) is null)
        {
            Note("That folder needs a manifest.json.");
            return;
        }
        if (!_state.ExtensionPaths.Contains(path, StringComparer.Ordinal))
            _state.ExtensionPaths.Add(path);
        WebEngine.SetExtensions(_state.ExtensionPaths);
        Store.Current.Save();
        RefreshExtensions();
        Note("Extension loaded. Reload open tabs to run it.");
    }

    void BootButton_Click(object? sender, RoutedEventArgs e) => _ = BootAsync();

    void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _memoryTimer.Stop();
        _noteTimer.Stop();
        _downloadTimer.Stop();
        if (!_private)
        {
            Snapshot();
            SaveBounds();
            Store.Current.Save();
        }
        _vault.Lock();
        WebSurface.Shutdown();
        if (_store != IntPtr.Zero)
        {
            WebEngine.ReleaseStore(_store);
            _store = IntPtr.Zero;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Window_Closing(this, e);
        base.OnClosing(e);
    }

    sealed class HistoryRow
    {
        public required string Title { get; init; }
        public required string Url { get; init; }
        public required string When { get; init; }
    }

    sealed class MemoryRow
    {
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public required string Amount { get; init; }
    }

    sealed class VaultPageState
    {
        public string Kind { get; init; } = "vault-state";
        public bool Exists { get; init; }
        public bool Unlocked { get; init; }
        public bool Windows { get; init; }
        public bool TouchId { get; init; }
        public string Platform { get; init; } = "mac";
        public string? Error { get; init; }
        public List<VaultEntry>? Entries { get; init; }
    }

    sealed class SuggestRow
    {
        public required string Title { get; init; }
        public required string Url { get; init; }
        public required string Kind { get; init; }
    }
}
