using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Cove;

public partial class MainWindow : Window
{
    readonly bool _private;
    readonly AppState _state;
    readonly Vault _vault;
    readonly ObservableCollection<WorkspaceVm> _workspaces = new();
    readonly ObservableCollection<Bookmark> _bookmarks;
    readonly ObservableCollection<DownloadVm> _downloads = new();
    readonly List<string> _closed = [];
    bool _trimming;
    readonly DispatcherTimer _saveTimer;
    readonly DispatcherTimer _memoryTimer;
    readonly DispatcherTimer _noteTimer;
    WorkspaceVm? _current;
    bool _switching;
    bool _ready;
    int _tabGuard;
    bool _sidebarOpen = true;
    string _dock = "left";
    Point _brandStart;
    bool _brandDown;
    bool _brandDrag;
    bool _videoFullscreen;
    WindowState _beforeFullscreen;
    Rect _beforeBounds;
    string? _pendingHost;
    string? _pendingUser;
    string? _pendingPass;

    public MainWindow(bool isPrivate = false)
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
            StatusNote.Visibility = Visibility.Collapsed;
        };

        if (_private) PrivateBadge.Visibility = Visibility.Visible;
        ApplyBounds();
        RamSlider.Value = Math.Clamp(_state.RamCapMb, 1024, 16384);
        _dock = _state.SidebarDock is "right" or "bottom" ? _state.SidebarDock : "left";
        if (_state.SidebarSize < 120) _state.SidebarSize = 188;
        SetSidebar(_state.SidebarOpen);
        ShowPanel("workspaces");
        RefreshHistory();
        Loaded += async (_, _) =>
        {
            App.Log("loaded");
            await BootAsync();
        };
    }

    async Task BootAsync()
    {
        try
        {
            await BrowserHost.GetAsync();
            if (_private || _state.Workspaces.Count == 0)
            {
                var fresh = new WorkspaceVm("Main", string.IsNullOrWhiteSpace(_state.Accent) ? Theme.AccentHex : _state.Accent);
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
                        var url = string.IsNullOrWhiteSpace(tab.Url) || tab.Url.Equals("cove://newtab", StringComparison.OrdinalIgnoreCase)
                            ? NewTabUrl()
                            : tab.Url;
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
            await RefreshExtensionsAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            App.Log("webview2 runtime missing");
            BootText.Text = "Cove needs the Microsoft Edge WebView2 Runtime.";
            BootButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
            BootText.Text = ex.Message;
        }
    }

    async Task<TabVm> CreateTabAsync(WorkspaceVm ws, string url, bool select, int insertAt = -1)
    {
        var tab = new TabVm { Title = "New tab" };
        var index = insertAt;
        if (index < 0)
        {
            var current = ReferenceEquals(ws, _current) ? ActiveTab() : null;
            index = current is null ? ws.Tabs.Count : ws.Tabs.IndexOf(current) + 1;
        }
        if (index < 0 || index > ws.Tabs.Count) index = ws.Tabs.Count;
        ws.Tabs.Insert(index, tab);
        WebHost.Children.Add(tab.View);
        try
        {
            tab.View.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x12, 0x13, 0x16);
        }
        catch
        {
            // The color is cosmetic. Navigation still works without it.
        }

        var env = await BrowserHost.GetAsync();
        if (_private)
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await tab.View.EnsureCoreWebView2Async(env, options);
        }
        else
        {
            await tab.View.EnsureCoreWebView2Async(env);
        }

        Wire(tab);
        var core = tab.View.CoreWebView2;
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(WebScripts.Bootstrap); }
        catch { /* The page script is also applied after each navigation. */ }
        core.Navigate(string.IsNullOrWhiteSpace(url) ? NewTabUrl() : url);
        if (select)
        {
            _current = ws;
            TabList.ItemsSource = ws.Tabs;
            ShowTab(tab);
        }
        else
        {
            tab.View.Visibility = Visibility.Collapsed;
        }
        ScheduleSave();
        return tab;
    }

    void Wire(TabVm tab)
    {
        var core = tab.View.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;
        core.SetVirtualHostNameToFolderMapping("cove.local", BrowserHost.PagesDirectory, CoreWebView2HostResourceAccessKind.Deny);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);

        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            {
                tab.PageHost = uri.Host;
                tab.Url = uri.ToString();
            }
            tab.Blocked = 0;
            tab.IsSleeping = false;
            if (IsCurrent(tab))
            {
                Progress.Visibility = Visibility.Visible;
                UpdateChrome();
            }
        };
        core.SourceChanged += (_, _) =>
        {
            tab.Url = core.Source;
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)) tab.PageHost = uri.Host;
            if (IsCurrent(tab)) UpdateChrome();
            if (ReferenceEquals(ActiveTab(), tab)) ScheduleSave();
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (IsCurrent(tab)) Progress.Visibility = Visibility.Collapsed;
            if (e.IsSuccess) RememberHistory(tab);
            _ = SetAdsFlagAsync(tab);
            try { _ = core.ExecuteScriptAsync(WebScripts.Bootstrap); }
            catch { /* The document can close before the script runs. */ }
            if (IsCurrent(tab)) UpdateChrome();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New tab" : core.DocumentTitle;
            if (IsCurrent(tab)) Title = DisplayTitle(tab);
        };
        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream is null) return;
                await using (stream)
                    tab.Favicon = BitmapFrom(stream);
            }
            catch
            {
                // Some pages never publish a favicon.
            }
        };
        core.HistoryChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.Source)) tab.Url = core.Source;
            if (IsCurrent(tab)) UpdateChrome();
            if (ReferenceEquals(ActiveTab(), tab)) ScheduleSave();
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (_current is null) return;
            var destination = string.IsNullOrWhiteSpace(e.Uri) ? NewTabUrl() : e.Uri;
            _ = CreateTabAsync(_current, destination, select: true);
        };
        core.DownloadStarting += (_, e) => OnDownload(e);
        core.WebMessageReceived += (_, e) => OnMessage(tab, e);
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (!IsCurrent(tab)) return;
            var on = core.ContainsFullScreenElement;
            if (Dispatcher.CheckAccess()) SetVideoFullscreen(on);
            else Dispatcher.Invoke(() => SetVideoFullscreen(on));
        };
        core.WebResourceRequested += (_, e) => OnResource(tab, e);
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) return;
            try { core.Reload(); } catch { /* The view may already be closing. */ }
        };
    }

    void OnResource(TabVm tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
        if (!AdBlock.ShouldBlock(uri, tab.PageHost, _state.AdBlockEnabled, _state.AdBlockAllow)) return;
        try
        {
            e.Response = tab.View.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            tab.Blocked++;
            if (IsCurrent(tab)) UpdateShield();
        }
        catch
        {
            // A request can finish before the response is attached.
        }
    }

    void OnMessage(TabVm tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch { return; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("kind", out var kindEl)) return;
            var kind = kindEl.GetString();
            if (kind is not null && kind.StartsWith("vault-", StringComparison.Ordinal))
            {
                HandleVault(tab, kind, doc.RootElement, e.Source);
                return;
            }
            if (kind == "here")
            {
                var url = doc.RootElement.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var here) && here.Scheme is "https" or "http")
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
                var core = tab.View.CoreWebView2;
                if (!IsCurrent(tab) || core is null) return;
                var fromTop = string.Equals(e.Source, core.Source, StringComparison.OrdinalIgnoreCase);
                if (!fromTop) return;
                var on = doc.RootElement.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                SetVideoFullscreen(on || core.ContainsFullScreenElement);
                return;
            }
            if (kind == "search")
            {
                if (e.Source is null || !e.Source.Contains("cove.local", StringComparison.OrdinalIgnoreCase)) return;
                var query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                if (IsCurrent(tab)) NavigateActive(query);
                else tab.View.CoreWebView2?.Navigate(Resolve(query));
                return;
            }
            if (kind != "login" || _private) return;
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var uri)) return;
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
            SaveBar.Visibility = Visibility.Visible;
        }
    }

    void OnDownload(CoreWebView2DownloadStartingEventArgs e)
    {
        var suggested = Path.GetFileName(e.ResultFilePath);
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "download";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, suggested);
        e.ResultFilePath = path;
        e.Handled = true;
        var item = new DownloadVm { Name = Path.GetFileName(path), Path = path, Detail = "Starting…", Running = true };
        item.Operation = e.DownloadOperation;
        _downloads.Insert(0, item);
        e.DownloadOperation.BytesReceivedChanged += (_, _) => Dispatcher.InvokeAsync(() => UpdateDownload(item));
        e.DownloadOperation.StateChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            UpdateDownload(item);
            if (item.Operation?.State == CoreWebView2DownloadState.Completed && !_private)
            {
                _state.Downloads.Insert(0, new DownloadRecord { Name = item.Name, Path = item.Path, When = DateTimeOffset.Now });
                if (_state.Downloads.Count > 40) _state.Downloads.RemoveAt(_state.Downloads.Count - 1);
                Store.Current.Save();
            }
        });
        Note($"Downloading {item.Name}");
    }

    static void UpdateDownload(DownloadVm item)
    {
        var op = item.Operation;
        if (op is null) return;
        var total = op.TotalBytesToReceive;
        if (total is > 0)
        {
            item.Indeterminate = false;
            item.Progress = Math.Clamp(op.BytesReceived * 100d / total.Value, 0, 100);
            item.Detail = $"{FormatBytes(op.BytesReceived)} of {FormatBytes(total.Value)}";
        }
        else
        {
            item.Indeterminate = op.State == CoreWebView2DownloadState.InProgress;
            item.Detail = FormatBytes(op.BytesReceived);
        }
        item.Running = op.State == CoreWebView2DownloadState.InProgress;
        if (op.State == CoreWebView2DownloadState.Completed) item.Detail = "Saved";
        if (op.State == CoreWebView2DownloadState.Interrupted) item.Detail = "Stopped";
    }

    void ShowTab(TabVm tab)
    {
        var ws = WorkspaceOf(tab);
        if (ws is null) return;
        _current = ws;
        foreach (var item in _workspaces) item.IsActive = ReferenceEquals(item, ws);
        foreach (var other in AllTabs())
            other.View.Visibility = ReferenceEquals(other, tab) ? Visibility.Visible : Visibility.Collapsed;
        var core = tab.View.CoreWebView2;
        if (core is { IsSuspended: true })
        {
            core.Resume();
            tab.IsSleeping = false;
        }
        tab.LastActive = Environment.TickCount64;
        tab.Seen = DateTimeOffset.Now;
        SetVideoFullscreen(core?.ContainsFullScreenElement == true);
        ws.ActiveIndex = Math.Max(0, ws.Tabs.IndexOf(tab));
        _switching = true;
        WorkspaceList.SelectedItem = ws;
        if (!ReferenceEquals(TabList.ItemsSource, ws.Tabs)) TabList.ItemsSource = ws.Tabs;
        TabList.SelectedItem = tab;
        _switching = false;
        BootPanel.Visibility = Visibility.Collapsed;
        UpdateChrome();
        if (_ready) Persist();
        _tabGuard++;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            KeepTab(tab);
            _tabGuard = Math.Max(0, _tabGuard - 1);
        });
    }

    void KeepTab(TabVm tab)
    {
        var ws = WorkspaceOf(tab);
        if (ws is null || !ReferenceEquals(_current, ws) || !ws.Tabs.Contains(tab)) return;
        _switching = true;
        if (!ReferenceEquals(TabList.ItemsSource, ws.Tabs)) TabList.ItemsSource = ws.Tabs;
        TabList.SelectedItem = tab;
        foreach (var other in AllTabs())
            other.View.Visibility = ReferenceEquals(other, tab) ? Visibility.Visible : Visibility.Collapsed;
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
        if (tab?.View.CoreWebView2 is null)
        {
            if (_current is not null) _ = CreateTabAsync(_current, url, select: true);
            return;
        }
        if (tab.View.CoreWebView2.IsSuspended) tab.View.CoreWebView2.Resume();
        tab.View.CoreWebView2.Navigate(url);
    }

    void UpdateChrome()
    {
        var tab = ActiveTab();
        var core = tab?.View.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        if (tab is null)
        {
            Title = _private ? "Private — Cove" : "Cove";
            return;
        }
        Title = DisplayTitle(tab);
        if (!AddressBox.IsKeyboardFocused)
            AddressBox.Text = IsNewTab(tab.Url) ? "" : tab.Url;
        var marked = _state.Bookmarks.Any(b => b.Url == tab.Url);
        StarButton.Content = marked ? "\uE735" : "\uE734";
        UpdateShield();
        if (tab.View.CoreWebView2 is not null)
            Progress.Visibility = Visibility.Collapsed;
    }

    void UpdateShield()
    {
        var tab = ActiveTab();
        var allowed = tab is not null && _state.AdBlockAllow.Contains(tab.PageHost, StringComparer.OrdinalIgnoreCase);
        var on = _state.AdBlockEnabled && !allowed && !string.IsNullOrEmpty(tab?.PageHost);
        ShieldButton.Foreground = on ? Theme.Accent : (Brush)FindResource("Muted");
        ShieldButton.ToolTip = !_state.AdBlockEnabled
            ? "Blocking is off. Click to turn it on."
            : allowed
                ? $"Allowed on {tab?.PageHost}. Click to block again."
                : "Blocking ads and trackers. Click to allow this site.";
        var blocked = tab?.Blocked ?? 0;
        ShieldCount.Text = blocked > 0 ? blocked.ToString() : "";
        ShieldCount.Visibility = blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task SetAdsFlagAsync(TabVm tab)
    {
        var core = tab.View.CoreWebView2;
        if (core is null || core.IsSuspended) return;
        var on = _state.AdBlockEnabled && !_state.AdBlockAllow.Contains(tab.PageHost, StringComparer.OrdinalIgnoreCase);
        try { await core.ExecuteScriptAsync($"window.__coveAdsOn = {(on ? "true" : "false")};"); }
        catch { /* The page may have navigated away. */ }
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
        var filter = HistorySearch.Text.Trim();
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
        WebHost.Children.Remove(tab.View);
        tab.View.Dispose();
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

    bool HandleShortcut(int vk, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && shift && vk == 0x4E)
        {
            new MainWindow(isPrivate: true).Show();
            return true;
        }
        if (ctrl && shift && vk == 0x54)
        {
            if (_closed.Count == 0 || _current is null) return true;
            var url = _closed[^1];
            _closed.RemoveAt(_closed.Count - 1);
            _ = CreateTabAsync(_current, url, select: true);
            return true;
        }
        if (ctrl && !shift && vk == 0x54)
        {
            if (_current is not null) _ = CreateTabAsync(_current, NewTabUrl(), select: true);
            return true;
        }
        if (ctrl && !shift && vk == 0x57)
        {
            var tab = ActiveTab();
            if (tab is not null) CloseTab(tab);
            return true;
        }
        if (ctrl && vk == 0x4C)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            return true;
        }
        if (ctrl && vk == 0x44)
        {
            ToggleBookmark();
            return true;
        }
        if (ctrl && vk == 0x09 && _current is { Tabs.Count: > 1 })
        {
            var tabs = _current.Tabs;
            var index = Math.Max(0, tabs.IndexOf(ActiveTab()!));
            var next = shift ? (index - 1 + tabs.Count) % tabs.Count : (index + 1) % tabs.Count;
            ShowTab(tabs[next]);
            return true;
        }
        if ((ctrl && vk == 0x52) || vk == 0x74)
        {
            ActiveTab()?.View.CoreWebView2?.Reload();
            return true;
        }
        if (alt && vk == 0x25)
        {
            if (ActiveTab()?.View.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
            return true;
        }
        if (alt && vk == 0x27)
        {
            if (ActiveTab()?.View.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
            return true;
        }
        if (vk == 0x1B && !ctrl && !alt && Progress.Visibility == Visibility.Visible)
        {
            ActiveTab()?.View.CoreWebView2?.Stop();
            Progress.Visibility = Visibility.Collapsed;
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
        var core = tab.View.CoreWebView2;
        if (core is null) return;
        if (core.IsSuspended) core.Resume();
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["expression"] = WebScripts.Popout,
                ["userGesture"] = true,
                ["awaitPromise"] = true,
                ["returnByValue"] = true
            });
            var raw = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", payload);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value))
            {
                Note("No video on this page.");
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
    }

    async Task TrimMemoryAsync()
    {
        await UpdateUsageAsync();
        long bytes;
        try { bytes = MemoryUsage.WorkingSet(); }
        catch { return; }
        var cap = (long)_state.RamCapMb * 1024L * 1024L;
        RamLabel.Text = $"{FormatBytes(bytes)} in use · cap {FormatBytes(cap)}";
        if (bytes <= cap) return;
        var active = ActiveTab();
        foreach (var tab in AllTabs().OrderBy(t => t.LastActive))
        {
            if (ReferenceEquals(tab, active)) continue;
            var view = tab.View.CoreWebView2;
            if (view is null || view.IsSuspended || view.IsDocumentPlayingAudio) continue;
            try { tab.IsSleeping = await view.TrySuspendAsync(); }
            catch { /* Suspending is best-effort. */ }
            if (MemoryUsage.WorkingSet() <= cap) break;
        }
    }

    async Task UpdateUsageAsync()
    {
        var rows = new List<MemoryRow>();
        try
        {
            var env = await BrowserHost.GetAsync();
            var infos = await env.GetProcessExtendedInfosAsync();
            var pidBytes = new Dictionary<int, long>();
            var framePid = new Dictionary<uint, int>();
            foreach (var info in infos)
            {
                var proc = info.ProcessInfo;
                var pid = proc.ProcessId;
                if (!pidBytes.ContainsKey(pid))
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        pidBytes[pid] = process.WorkingSet64;
                    }
                    catch { pidBytes[pid] = 0; }
                }
                if (proc.Kind != CoreWebView2ProcessKind.Renderer) continue;
                foreach (var frame in info.AssociatedFrameInfos)
                {
                    if (frame.FrameId != 0) framePid[frame.FrameId] = pid;
                }
            }

            var tabs = AllTabs().ToList();
            var owners = new List<(TabVm Tab, int Pid)>();
            foreach (var tab in tabs)
            {
                var core = tab.View.CoreWebView2;
                var pid = core is not null && core.FrameId != 0 && framePid.TryGetValue(core.FrameId, out var found) ? found : 0;
                owners.Add((tab, pid));
            }
            var shared = owners.Where(item => item.Pid != 0).GroupBy(item => item.Pid).ToDictionary(group => group.Key, group => group.Count());
            foreach (var (tab, pid) in owners)
            {
                string amount;
                if (tab.View.CoreWebView2 is { IsSuspended: true }) amount = "Asleep";
                else if (pid == 0 || !pidBytes.TryGetValue(pid, out var used) || used <= 0) amount = "—";
                else amount = FormatBytes(used / Math.Max(1, shared[pid]));
                rows.Add(new MemoryRow { Kind = "Tab", Name = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title, Amount = amount });
            }

            var profile = tabs.Select(t => t.View.CoreWebView2?.Profile).FirstOrDefault(p => p is not null);
            if (profile is not null && !_private)
            {
                var extensions = await profile.GetBrowserExtensionsAsync();
                var commands = new Dictionary<int, string>();
                await Task.Run(() =>
                {
                    foreach (var pid in pidBytes.Keys)
                        commands[pid] = MemoryUsage.CommandLine(pid);
                });
                foreach (var ext in extensions)
                {
                    long used = 0;
                    var hits = 0;
                    foreach (var (pid, command) in commands)
                    {
                        if (command.Contains(ext.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            used += pidBytes.GetValueOrDefault(pid);
                            hits++;
                        }
                    }
                    rows.Add(new MemoryRow { Kind = "Extension", Name = ext.Name, Amount = hits == 0 ? "Shared" : FormatBytes(used) });
                }
            }
        }
        catch
        {
            // The list stays empty until the browser process answers.
        }
        MemoryList.ItemsSource = rows;
    }

    async Task RefreshExtensionsAsync()
    {
        ExtensionHost.Children.Clear();
        var profile = _workspaces.SelectMany(w => w.Tabs).Select(t => t.View.CoreWebView2?.Profile).FirstOrDefault(p => p is not null);
        if (profile is null || _private) return;
        try
        {
            var extensions = await profile.GetBrowserExtensionsAsync();
            if (extensions.Count == 0)
            {
                ExtensionHost.Children.Add(new TextBlock { Text = "No extensions loaded.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 8) });
                return;
            }
            foreach (var ext in extensions)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var remove = new Button { Content = "Remove", Tag = ext };
                remove.Click += async (_, _) =>
                {
                    try
                    {
                        await ext.RemoveAsync();
                        await RefreshExtensionsAsync();
                    }
                    catch (Exception ex) { Note(ex.Message); }
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = ext.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                ExtensionHost.Children.Add(row);
            }
        }
        catch
        {
            ExtensionHost.Children.Add(new TextBlock { Text = "Extensions are unavailable in this runtime.", Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
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
        _state.Accent = Theme.AccentHex;
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
            var right = _state.Left + _state.Width;
            var bottom = _state.Top + _state.Height;
            var onScreen = right > SystemParameters.VirtualScreenLeft + 80
                && _state.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                && bottom > SystemParameters.VirtualScreenTop + 80
                && _state.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _state.Left;
                Top = _state.Top;
                Width = _state.Width;
                Height = _state.Height;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_state.Maximized) WindowState = WindowState.Maximized;
    }

    void SaveBounds()
    {
        if (_videoFullscreen)
        {
            _state.Maximized = _beforeFullscreen == WindowState.Maximized;
            if (_beforeBounds.Width >= 800 && _beforeBounds.Height >= 500)
            {
                _state.Left = _beforeBounds.Left;
                _state.Top = _beforeBounds.Top;
                _state.Width = _beforeBounds.Width;
                _state.Height = _beforeBounds.Height;
            }
            return;
        }
        _state.Maximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width >= 800 && bounds.Height >= 500)
        {
            _state.Left = bounds.Left;
            _state.Top = bounds.Top;
            _state.Width = bounds.Width;
            _state.Height = bounds.Height;
        }
    }

    void SetSidebar(bool open)
    {
        _sidebarOpen = open;
        _state.SidebarOpen = open;
        ApplyChrome();
        HighlightRail(CurrentPanel());
    }

    void ApplyChrome()
    {
        _dock = _dock is "right" or "bottom" ? _dock : "left";
        const double rail = 36;
        var openSize = Math.Clamp(_state.SidebarSize, _dock == "bottom" ? 120 : 150, _dock == "bottom" ? 420 : 520);
        var size = _sidebarOpen ? openSize : rail;
        var star = new GridLength(1, GridUnitType.Star);

        Root.ColumnDefinitions.Clear();
        Root.RowDefinitions.Clear();

        if (_dock == "bottom")
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Root.RowDefinitions.Add(new RowDefinition { Height = star });
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition
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
            SidebarSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarSplitter.ResizeDirection = GridResizeDirection.Rows;
            SidebarSplitter.Cursor = Cursors.SizeNS;
            LayoutSidebar(horizontal: true);
        }
        else if (_dock == "right")
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = star });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition
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
            SidebarSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            SidebarSplitter.ResizeDirection = GridResizeDirection.Columns;
            SidebarSplitter.Cursor = Cursors.SizeWE;
            LayoutSidebar(horizontal: false);
        }
        else
        {
            Root.RowDefinitions.Add(new RowDefinition { Height = star });
            Root.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(size),
                MinWidth = _sidebarOpen ? 150 : rail,
                MaxWidth = _sidebarOpen ? 560 : rail
            });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = star });
            Place(SidebarShell, 0, 0);
            Place(SidebarSplitter, 1, 0);
            Place(Browser, 2, 0);
            SidebarSplitter.Height = double.NaN;
            SidebarSplitter.Width = 5;
            SidebarSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            SidebarSplitter.ResizeDirection = GridResizeDirection.Columns;
            SidebarSplitter.Cursor = Cursors.SizeWE;
            LayoutSidebar(horizontal: false);
        }

        SidebarSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        SidebarSplitter.Visibility = _sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        PanelHost.Visibility = _sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    static void Place(UIElement element, int column, int row)
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
        if (!_sidebarOpen) return;
        var size = _dock switch
        {
            "bottom" when Root.RowDefinitions.Count > 2 => Root.RowDefinitions[2].ActualHeight,
            "right" when Root.ColumnDefinitions.Count > 2 => Root.ColumnDefinitions[2].ActualWidth,
            "left" when Root.ColumnDefinitions.Count > 0 => Root.ColumnDefinitions[0].ActualWidth,
            _ => 0
        };
        if (size >= 110) _state.SidebarSize = size;
    }

    void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        RememberSidebarSize();
        ScheduleSave();
    }

    void Brand_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _brandStart = e.GetPosition(this);
        _brandDown = true;
        _brandDrag = false;
        BrandButton.CaptureMouse();
        e.Handled = true;
    }

    void Brand_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_brandDown || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if ((_brandStart - pos).Length > 10) _brandDrag = true;
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

    void Brand_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_brandDown) return;
        _brandDown = false;
        if (BrandButton.IsMouseCaptured) BrandButton.ReleaseMouseCapture();
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
        if (pos.Y > ActualHeight * 0.68) return "bottom";
        if (pos.X < ActualWidth * 0.28) return "left";
        if (pos.X > ActualWidth * 0.72) return "right";
        return _dock;
    }

    void ShowPanel(string name)
    {
        PanelWorkspaces.Visibility = name == "workspaces" ? Visibility.Visible : Visibility.Collapsed;
        PanelBookmarks.Visibility = name == "bookmarks" ? Visibility.Visible : Visibility.Collapsed;
        PanelHistory.Visibility = name == "history" ? Visibility.Visible : Visibility.Collapsed;
        PanelDownloads.Visibility = name == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        PanelExtensions.Visibility = name == "extensions" ? Visibility.Visible : Visibility.Collapsed;
        PanelLimiter.Visibility = name == "limiter" ? Visibility.Visible : Visibility.Collapsed;
        HighlightRail(name);
    }

    void HighlightRail(string name)
    {
        foreach (var button in new[] { RailWorkspaces, RailBookmarks, RailHistory, RailDownloads, RailExtensions, RailLimiter })
        {
            var on = (string)button.Tag == name && _sidebarOpen;
            button.Foreground = on ? Theme.Accent : (Brush)FindResource("Ink");
        }
    }

    string CurrentPanel()
    {
        if (PanelBookmarks.Visibility == Visibility.Visible) return "bookmarks";
        if (PanelHistory.Visibility == Visibility.Visible) return "history";
        if (PanelDownloads.Visibility == Visibility.Visible) return "downloads";
        if (PanelExtensions.Visibility == Visibility.Visible) return "extensions";
        if (PanelLimiter.Visibility == Visibility.Visible) return "limiter";
        return "workspaces";
    }

    void Note(string text)
    {
        StatusNote.Text = text;
        StatusNote.Visibility = Visibility.Visible;
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    void ClearPending()
    {
        _pendingHost = _pendingUser = _pendingPass = null;
        SaveBar.Visibility = Visibility.Collapsed;
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
    string NewTabUrl() => "https://cove.local/newtab.html?accent=" + Theme.AccentHex.TrimStart('#');

    static string Collapse(string url) =>
        url.Contains("cove.local/newtab", StringComparison.OrdinalIgnoreCase) ? "cove://newtab" : url;

    static bool IsNewTab(string url) =>
        url.Contains("cove.local/newtab", StringComparison.OrdinalIgnoreCase) || url.Equals("cove://newtab", StringComparison.OrdinalIgnoreCase);

    static bool IsWeb(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && !uri.Host.Equals("cove.local", StringComparison.OrdinalIgnoreCase);

    static string Resolve(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "https://cove.local/newtab.html";
        if (input.StartsWith("cove://newtab", StringComparison.OrdinalIgnoreCase)) return "https://cove.local/newtab.html";
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http") return absolute.ToString();
        var host = input.Split('/')[0];
        var looksLikeHost = !input.Contains(' ') && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) || host.Contains('.'));
        if (looksLikeHost) return "https://" + input;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        if (local.Date == DateTime.Today) return local.ToString("h:mm tt");
        if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        return local.ToString("MMM d");
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

    static BitmapImage BitmapFrom(Stream stream)
    {
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = copy;
        image.EndInit();
        image.Freeze();
        return image;
    }

    static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node) ?? (node as FrameworkElement)?.Parent;
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
            _beforeBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            SidebarShell.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            TabChrome.Visibility = Visibility.Collapsed;
            AddressChrome.Visibility = Visibility.Collapsed;
            Progress.Visibility = Visibility.Collapsed;
            SaveBar.Visibility = Visibility.Collapsed;
            TabRowDef.Height = new GridLength(0);
            AddressRowDef.Height = new GridLength(0);
            ProgressRowDef.Height = new GridLength(0);
            Root.ColumnDefinitions.Clear();
            Root.RowDefinitions.Clear();
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Place(Browser, 0, 0);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0)
            });
            var screen = MonitorBounds();
            WindowState = WindowState.Normal;
            Left = screen.Left;
            Top = screen.Top;
            Width = screen.Width;
            Height = screen.Height;
            Root.Margin = new Thickness(0);
        }
        else
        {
            TabChrome.Visibility = Visibility.Visible;
            AddressChrome.Visibility = Visibility.Visible;
            TabRowDef.Height = new GridLength(40);
            AddressRowDef.Height = new GridLength(40);
            ProgressRowDef.Height = new GridLength(2);
            SidebarShell.Visibility = Visibility.Visible;
            ApplyCaption();
            ApplyChrome();
            if (_beforeFullscreen == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                if (_beforeBounds.Width >= 800 && _beforeBounds.Height >= 500)
                {
                    Left = _beforeBounds.Left;
                    Top = _beforeBounds.Top;
                    Width = _beforeBounds.Width;
                    Height = _beforeBounds.Height;
                }
            }
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    Rect MonitorBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var source = PresentationSource.FromVisual(this);
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var origin = toDip.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
        var far = toDip.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));
        return new Rect(origin, far);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _videoFullscreen)
        {
            _ = ActiveTab()?.View.CoreWebView2?.ExecuteScriptAsync("document.exitFullscreen?.()");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && AddressBox.IsKeyboardFocused)
        {
            ActiveTab()?.View.Focus();
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (HandleShortcut(KeyInterop.VirtualKeyFromKey(key), mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt)))
            e.Handled = true;
    }

    void Window_StateChanged(object? sender, EventArgs e)
    {
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        ApplyCaption();
    }

    void ApplyCaption()
    {
        if (_videoFullscreen) return;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40 + (WindowState == WindowState.Maximized ? 7 : 0),
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0)
        });
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _memoryTimer.Stop();
        _noteTimer.Stop();
        if (!_private)
        {
            Snapshot();
            SaveBounds();
            Store.Current.Save();
        }
        _vault.Lock();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        var corner = 2;
        DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
    }

    void Drag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source && (Ancestor<Button>(source) is not null || Ancestor<TextBox>(source) is not null || Ancestor<ListBoxItem>(source) is not null))
            return;
        if (e.ClickCount == 2)
        {
            ToggleMax();
            return;
        }
        try { DragMove(); } catch { /* DragMove throws if the window is already moving. */ }
    }

    void ToggleMax() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Back_Click(object sender, RoutedEventArgs e) { if (ActiveTab()?.View.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); }
    void Forward_Click(object sender, RoutedEventArgs e) { if (ActiveTab()?.View.CoreWebView2 is { CanGoForward: true } core) core.GoForward(); }
    void Reload_Click(object sender, RoutedEventArgs e) => ActiveTab()?.View.CoreWebView2?.Reload();
    void NewTab_Click(object sender, RoutedEventArgs e) { if (_current is not null) _ = CreateTabAsync(_current, NewTabUrl(), select: true); }
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => ToggleMax();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Bookmark_Click(object sender, RoutedEventArgs e) => ToggleBookmark();

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TabVm tab) CloseTab(tab);
        e.Handled = true;
    }

    void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || _tabGuard > 0) return;
        if (TabList.SelectedItem is TabVm tab) ShowTab(tab);
    }

    void TabList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (Ancestor<ListBoxItem>(source)?.DataContext is TabVm tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
    }

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        NavigateActive(AddressBox.Text);
        ActiveTab()?.View.Focus();
        e.Handled = true;
    }

    void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    void WorkspaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        if (WorkspaceList.SelectedItem is WorkspaceVm ws) SwitchWorkspace(ws);
    }

    void WorkspaceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WorkspaceList.SelectedItem is not WorkspaceVm ws) return;
        var name = Prompts.Ask(this, "Rename workspace", ws.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        ws.Name = name;
        ScheduleSave();
    }

    void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompts.Ask(this, "New workspace", "Workspace");
        if (string.IsNullOrWhiteSpace(name)) return;
        var color = Theme.Swatches[_workspaces.Count % Theme.Swatches.Length];
        var ws = new WorkspaceVm(name, color);
        _workspaces.Add(ws);
        SwitchWorkspace(ws);
    }

    void DeleteWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WorkspaceVm ws) return;
        if (_workspaces.Count <= 1)
        {
            Note("Keep at least one workspace.");
            return;
        }
        if (ws.Tabs.Count > 1 && !Prompts.Confirm(this, "Remove workspace", $"Close {ws.Name} and its tabs?")) return;
        foreach (var tab in ws.Tabs.ToList())
        {
            WebHost.Children.Remove(tab.View);
            tab.View.Dispose();
        }
        ws.Tabs.Clear();
        var index = _workspaces.IndexOf(ws);
        _workspaces.Remove(ws);
        SwitchWorkspace(_workspaces[Math.Clamp(index - 1, 0, _workspaces.Count - 1)]);
        e.Handled = true;
    }

    void CycleColor_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WorkspaceVm ws) return;
        var index = Array.IndexOf(Theme.Swatches, ws.ColorHex.ToUpperInvariant());
        ws.SetColor(Theme.Swatches[(index + 1) % Theme.Swatches.Length]);
        ScheduleSave();
        e.Handled = true;
    }

    void Rail_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
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

    void Bookmark_Open(object sender, MouseButtonEventArgs e)
    {
        if (BookmarkList.SelectedItem is Bookmark mark) NavigateActive(mark.Url);
    }

    void History_Open(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryRow row) NavigateActive(row.Url);
    }

    void HistorySearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistory();

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!Prompts.Confirm(this, "Clear history", "Remove every page from history on this PC?")) return;
        _state.History.Clear();
        RefreshHistory();
        Store.Current.Save();
    }

    void Download_Open(object sender, MouseButtonEventArgs e)
    {
        if (DownloadList.SelectedItem is not DownloadVm item || string.IsNullOrWhiteSpace(item.Path)) return;
        if (!File.Exists(item.Path))
        {
            Note("That file is no longer there.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadVm item && item.Running)
        {
            try { item.Operation?.Cancel(); } catch { /* Already finished. */ }
        }
        e.Handled = true;
    }

    void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _state.RamCapMb = (int)RamSlider.Value;
        ScheduleSave();
    }

    void Shield_Click(object sender, RoutedEventArgs e)
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
        var tab = ActiveTab();
        if (tab is not null) _ = SetAdsFlagAsync(tab);
        UpdateShield();
        Store.Current.Save();
    }

    void Shield_RightClick(object sender, MouseButtonEventArgs e)
    {
        _state.AdBlockEnabled = !_state.AdBlockEnabled;
        Note(_state.AdBlockEnabled ? "Blocking is on." : "Blocking is paused.");
        var tab = ActiveTab();
        if (tab is not null) _ = SetAdsFlagAsync(tab);
        UpdateShield();
        Store.Current.Save();
        e.Handled = true;
    }

    void SaveNotNow_Click(object sender, RoutedEventArgs e) => ClearPending();

    void SaveNever_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_pendingHost) && !_state.NeverSave.Contains(_pendingHost, StringComparer.OrdinalIgnoreCase))
            _state.NeverSave.Add(_pendingHost);
        ClearPending();
        Store.Current.Save();
    }

    void SavePassword_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingHost) || string.IsNullOrEmpty(_pendingPass)) return;
        if (!VaultWindow.Ensure(this, _vault)) return;
        _vault.Add(_pendingHost, _pendingUser ?? "", _pendingPass);
        ClearPending();
        foreach (var open in AllTabs())
        {
            if (open.Url.Contains("cove.local/passwords", StringComparison.OrdinalIgnoreCase))
                SendVault(open, null);
        }
        Note("Password saved on this PC.");
    }

    async void Passwords_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        const string url = "https://cove.local/passwords.html";
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
        string? error = null;
        var password = root.TryGetProperty("password", out var passEl) ? passEl.GetString() ?? "" : "";
        var mode = root.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : "cove";
        if (kind == "vault-setup")
        {
            if (_vault.Exists) error = "A vault already exists on this PC.";
            else if (mode == "windows")
            {
                if (!WindowsSignIn.Check(password)) error = "That isn't this PC's sign-in password. Use a Cove password instead.";
                else _vault.CreateForWindows();
            }
            else if (password.Length < 4) error = "Use at least 4 characters.";
            else _vault.Create(password);
        }
        else if (kind == "vault-unlock")
        {
            var ok = mode == "windows"
                ? WindowsSignIn.Check(password) && _vault.UnlockWithWindows()
                : _vault.Unlock(password);
            if (!ok) error = mode == "windows"
                ? "Couldn't unlock with this PC's password."
                : "That password didn't open the vault.";
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
            else if (!WindowsSignIn.Check(password)) error = "That isn't this PC's sign-in password.";
            else _vault.EnableWindowsUnlock();
        }
        SendVault(tab, error);
        if (kind is "vault-lock" or "vault-delete")
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
        var core = tab.View.CoreWebView2;
        if (core is null) return;
        var message = new VaultPageState
        {
            Exists = _vault.Exists,
            Unlocked = _vault.IsUnlocked,
            Windows = _vault.HasWindowsUnlock,
            Error = error,
            Entries = _vault.IsUnlocked ? _vault.Entries.ToList() : null
        };
        core.PostWebMessageAsJson(JsonSerializer.Serialize(message, WebJson));
    }

    static readonly JsonSerializerOptions WebJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    async void LoadExtension_Click(object sender, RoutedEventArgs e)
    {
        if (_private)
        {
            Note("Load extensions from the main window.");
            return;
        }
        var profile = ActiveTab()?.View.CoreWebView2?.Profile;
        if (profile is null)
        {
            Note("Open a page first.");
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder containing manifest.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await profile.AddBrowserExtensionAsync(dialog.FolderName);
            Note("Extension loaded.");
            await RefreshExtensionsAsync();
        }
        catch (Exception ex)
        {
            Note(ex.Message);
        }
    }

    void BootButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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
        public string? Error { get; init; }
        public List<VaultEntry>? Entries { get; init; }
    }
}
