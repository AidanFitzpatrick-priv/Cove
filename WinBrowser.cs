using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Cove;

/// <summary>WebView2 backend state: the shared environment and the ad-block filter (Windows only).</summary>
static class WinBackend
{
    const string VirtualHost = "cove.local";

    static Task<CoreWebView2Environment>? _env;
    static volatile bool _adsOn;
    static HashSet<string> _allow = new(StringComparer.OrdinalIgnoreCase);

    public static CoreWebView2Environment? Env { get; private set; }
    public static bool AdsOn => _adsOn;
    public static IReadOnlyCollection<string> Allow => _allow;

    public static async Task Start(bool block, IReadOnlyCollection<string> allow)
    {
        SetBlocking(block, allow);
        await Environment();
    }

    public static Task<CoreWebView2Environment> Environment() => _env ??= Create();

    static async Task<CoreWebView2Environment> Create()
    {
        try
        {
            var folder = Path.Combine(Store.Current.Root, "WebView2");
            Directory.CreateDirectory(folder);
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true,
                EnableTrackingPrevention = true
            };
            var env = await CoreWebView2Environment.CreateAsync(null, folder, options);
            Env = env;
            return env;
        }
        catch (Exception ex)
        {
            _env = null;
            if (ex is WebView2RuntimeNotFoundException)
                throw new InvalidOperationException("Cove needs the Microsoft Edge WebView2 Runtime. Install it from microsoft.com/edge/webview2 and try again.", ex);
            throw;
        }
    }

    public static void SetBlocking(bool enabled, IReadOnlyCollection<string> allow)
    {
        _allow = new HashSet<string>(allow.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        _adsOn = enabled;
    }

    public static CoreWebView2PreferredColorScheme PreferredScheme =>
        WebScripts.ForceDarkPages || !Theme.IsLight()
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;

    public static void ApplyPreferredColorScheme()
    {
        foreach (var page in WebEngine.Snapshot())
        {
            if (page is WinPageSession win) win.ApplyForceDark();
        }
    }

    /// <summary>Private windows get their own in-private profile name, carried as a GC handle.</summary>
    public static IntPtr CreatePrivateStore() =>
        GCHandle.ToIntPtr(GCHandle.Alloc("cove-private-" + Guid.NewGuid().ToString("N")));

    public static void ReleaseStore(IntPtr store)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(store);
            if (handle.IsAllocated) handle.Free();
        }
        catch
        {
            // Not one of ours.
        }
    }

    public static string ProfileFor(IntPtr store)
    {
        if (store != IntPtr.Zero)
        {
            try
            {
                if (GCHandle.FromIntPtr(store).Target is string name) return name;
            }
            catch
            {
                // Fall through to the shared private profile.
            }
        }
        return "cove-private";
    }

    /// <summary>cove://cove.local/x becomes https://cove.local/x, which the virtual host mapping serves.</summary>
    public static string ToHost(string url)
    {
        if (url.StartsWith("cove://newtab", StringComparison.OrdinalIgnoreCase)) return "https://" + VirtualHost + "/newtab.html";
        const string scheme = "cove://";
        if (url.StartsWith(scheme + VirtualHost, StringComparison.OrdinalIgnoreCase))
            return "https://" + url[scheme.Length..];
        return url;
    }

    /// <summary>The reverse of <see cref="ToHost"/>, so the rest of Cove sees the same cove:// URLs it does on macOS.</summary>
    public static string FromHost(string url)
    {
        const string prefix = "https://" + VirtualHost;
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (url.Length == prefix.Length || url[prefix.Length] is '/' or '?' or '#'))
            return "cove://" + url["https://".Length..];
        return url;
    }

    public static string VirtualHostName => VirtualHost;
}

sealed class WinPageSession : PageSession
{
    static int _downloadIds;

    readonly bool _private;
    readonly string _profile;
    readonly Dictionary<int, CoreWebView2DownloadOperation> _downloads = [];
    string _script;
    CoreWebView2Controller? _controller;
    CoreWebView2? _core;
    string? _scriptId;
    IntPtr _parent;
    bool _visible;
    bool _starting;
    bool _suspended;
    Action? _initial;

    public WinPageSession(bool isPrivate, IntPtr store, string script)
    {
        _private = isPrivate;
        _profile = isPrivate ? WinBackend.ProfileFor(store) : "";
        _script = script;
    }

    public override bool CanGoBack => !Dead && _core is { CanGoBack: true };
    public override bool CanGoForward => !Dead && _core is { CanGoForward: true };

    public override void Attach(IntPtr container)
    {
        if (Dead) return;
        _parent = container;
        if (_controller is null)
        {
            _ = StartAsync();
            return;
        }
        try { _controller.ParentWindow = container; }
        catch (Exception ex) { App.Log("webview2 reparent: " + ex.Message); }
        Layout();
    }

    public override void SetVisible(bool visible)
    {
        if (Dead) return;
        _visible = visible;
        if (visible && _suspended)
        {
            _suspended = false;
            Asleep = false;
            try { _core?.Resume(); } catch { /* The view may be closing. */ }
        }
        Layout();
    }

    internal override void Layout()
    {
        var controller = _controller;
        if (Dead || controller is null) return;
        try
        {
            if (_visible && _parent != IntPtr.Zero && Win32.GetClientRect(_parent, out var rect))
            {
                controller.Bounds = new System.Drawing.Rectangle(0, 0, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
                controller.IsVisible = true;
            }
            else
            {
                controller.IsVisible = false;
                controller.Bounds = System.Drawing.Rectangle.Empty;
            }
        }
        catch (Exception ex)
        {
            App.Log("webview2 layout: " + ex.Message);
        }
    }

    public void ApplyForceDark()
    {
        try
        {
            if (_core?.Profile is { } profile)
                profile.PreferredColorScheme = WinBackend.PreferredScheme;
        }
        catch
        {
            // Older runtimes don't expose preferred color scheme.
        }
        if (_core is not null) _ = ApplyAutoDarkAsync(_core);
    }

    static async Task ApplyAutoDarkAsync(CoreWebView2 core)
    {
        try
        {
            // Chromium auto-dark via CDP — solid alternative to CSS invert hacks.
            var json = WebScripts.ForceDarkPages ? "{\"enabled\":true}" : "{\"enabled\":false}";
            await core.CallDevToolsProtocolMethodAsync("Emulation.setAutoDarkModeOverride", json);
        }
        catch (Exception ex)
        {
            App.Log("webview2 auto-dark: " + ex.Message);
        }
    }

    public override void SetScript(string script)
    {
        _script = script;
        if (!Dead && _core is not null) _ = ReplaceScriptAsync(_core);
    }

    public override void Focus()
    {
        if (Dead || _controller is null || !_visible) return;
        try { _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); }
        catch { /* Not focusable right now. */ }
    }

    public override void GoBack() { if (CanGoBack) _core!.GoBack(); }
    public override void GoForward() { if (CanGoForward) _core!.GoForward(); }
    public override void Reload() { if (!Dead) _core?.Reload(); }
    public override void Stop() { if (!Dead) _core?.Stop(); }

    public override void CancelDownload(int id)
    {
        if (Dead || !_downloads.TryGetValue(id, out var operation)) return;
        try { operation.Cancel(); }
        catch { /* Already finished. */ }
    }

    public override void Navigate(string url)
    {
        if (Dead) return;
        Asleep = false;
        Url = url;
        _initial = () => NavigateNow(url);
        if (_core is null)
        {
            _ = StartAsync();
            return;
        }
        ResumeIfSuspended();
        Flush();
    }

    public override void LoadHtml(string html)
    {
        if (Dead) return;
        Asleep = false;
        _initial = () => _core!.NavigateToString(html);
        if (_core is null)
        {
            _ = StartAsync();
            return;
        }
        ResumeIfSuspended();
        Flush();
    }

    public override void Sleep()
    {
        if (Dead || Asleep) return;
        ResumeUrl = Url;
        Asleep = true;
        _initial = null;
        _visible = false;
        Layout();
        if (_core is not null) _ = SleepAsync(_core);
    }

    async Task SleepAsync(CoreWebView2 core)
    {
        try
        {
            if (await core.TrySuspendAsync())
            {
                if (Dead) return;
                if (Asleep)
                {
                    _suspended = true;
                    return;
                }
                core.Resume();
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log("webview2 suspend: " + ex.Message);
        }
        if (!Dead && Asleep)
        {
            try { core.Navigate("about:blank"); }
            catch { /* The view may be closing. */ }
        }
    }

    public override void Wake()
    {
        if (Dead || !Asleep) return;
        Asleep = false;
        if (_suspended)
        {
            ResumeIfSuspended();
            return;
        }
        if (!string.IsNullOrWhiteSpace(ResumeUrl)) Navigate(ResumeUrl);
    }

    public override void Run(string js)
    {
        var core = _core;
        if (Dead || core is null) return;
        _ = RunAsync(core, js);
    }

    static async Task RunAsync(CoreWebView2 core, string js)
    {
        try { await core.ExecuteScriptAsync(js); }
        catch { /* The page went away. */ }
    }

    public override void PostJson(string json)
    {
        if (Dead || _core is null) return;
        try { _core.PostWebMessageAsJson(json); }
        catch (Exception ex) { App.Log("webview2 post: " + ex.Message); }
    }

    public override async Task<string?> EvalJson(string expression)
    {
        var core = _core;
        if (Dead || core is null || _suspended) return null;
        try
        {
            // Runtime.evaluate can await the promise, which ExecuteScriptAsync cannot.
            var args = JsonSerializer.Serialize(new { expression = EvalWrapper(expression), awaitPromise = true, returnByValue = true });
            var reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            if (root.TryGetProperty("exceptionDetails", out _)) return null;
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText()
            };
        }
        catch (Exception ex)
        {
            App.Log("webview2 eval: " + ex.Message);
            return null;
        }
    }

    protected override void DisposeCore()
    {
        _initial = null;
        _downloads.Clear();
        var controller = _controller;
        _controller = null;
        _core = null;
        try { controller?.Close(); }
        catch { /* Already closed with its window. */ }
    }

    void ResumeIfSuspended()
    {
        if (!_suspended) return;
        _suspended = false;
        try { _core?.Resume(); }
        catch { /* The view may be closing. */ }
    }

    void Flush()
    {
        var action = _initial;
        _initial = null;
        if (action is null || _core is null) return;
        try { action(); }
        catch (Exception ex)
        {
            App.Log("webview2 navigate: " + ex.Message);
            OnNavigated(Url, false);
        }
    }

    void NavigateNow(string url) => _core!.Navigate(WinBackend.ToHost(url));

    async Task StartAsync()
    {
        if (_controller is not null || _starting || Dead || _parent == IntPtr.Zero) return;
        _starting = true;
        try
        {
            var env = await WinBackend.Environment();
            if (Dead) return;
            var options = env.CreateCoreWebView2ControllerOptions();
            if (_private)
            {
                options.IsInPrivateModeEnabled = true;
                options.ProfileName = _profile;
            }
            var controller = await env.CreateCoreWebView2ControllerAsync(_parent, options);
            if (Dead)
            {
                controller.Close();
                return;
            }
            _controller = controller;
            _core = controller.CoreWebView2;
            await ConfigureAsync(_core);
            Layout();
            Flush();
        }
        catch (Exception ex)
        {
            App.Log("webview2 start: " + ex);
            _controller = null;
            _core = null;
            OnNavigated(Url, false);
        }
        finally
        {
            _starting = false;
        }
    }

    async Task ConfigureAsync(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        try { core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced; }
        catch { /* Older runtimes don't expose the profile. */ }
        try { core.Profile.PreferredColorScheme = WinBackend.PreferredScheme; }
        catch { /* Older runtimes don't expose preferred color scheme. */ }
        await ApplyAutoDarkAsync(core);

        core.SetVirtualHostNameToFolderMapping(WinBackend.VirtualHostName, WebEngine.PagesDirectory, CoreWebView2HostResourceAccessKind.Allow);

        core.NavigationStarting += (_, e) =>
        {
            if (IsData(e.Uri)) return;
            OnNavigating(WinBackend.FromHost(e.Uri));
        };
        core.NavigationCompleted += (_, e) =>
        {
            var source = core.Source;
            if (IsData(source)) source = "";
            OnNavigated(WinBackend.FromHost(source), e.IsSuccess);
        };
        core.SourceChanged += (_, _) =>
        {
            var source = core.Source;
            if (!Asleep && !Dead && !IsData(source) && !source.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                Url = WinBackend.FromHost(source);
        };
        core.DocumentTitleChanged += (_, _) => OnTitle(core.DocumentTitle ?? "");
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OnPopup(WinBackend.FromHost(e.Uri));
        };
        core.WebMessageReceived += (_, e) => Forward(e.WebMessageAsJson, e.Source, mainFrame: true);
        core.FrameCreated += (_, e) =>
        {
            e.Frame.WebMessageReceived += (_, a) => Forward(a.WebMessageAsJson, a.Source, mainFrame: false);
        };
        core.DownloadStarting += (_, e) => OnDownloadStarting(e);
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) OnCrashed();
        };
        core.WebResourceRequested += (_, e) => FilterRequest(core, e);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);

        _scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(_script);
    }

    async Task ReplaceScriptAsync(CoreWebView2 core)
    {
        try
        {
            var previous = _scriptId;
            _scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(_script);
            if (previous is not null) core.RemoveScriptToExecuteOnDocumentCreated(previous);
        }
        catch (Exception ex)
        {
            App.Log("webview2 script: " + ex.Message);
        }
    }

    void Forward(string json, string source, bool mainFrame)
    {
        // Pages only send objects. Anything else would fail the shared "kind" parsing.
        if (string.IsNullOrEmpty(json) || json[0] != '{') return;
        OnMessage(json, WinBackend.FromHost(source ?? ""), mainFrame);
    }

    void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        var id = Interlocked.Increment(ref _downloadIds);
        var name = Path.GetFileName(e.ResultFilePath ?? "");
        var total = operation.TotalBytesToReceive is { } bytes ? (long)bytes : 0;
        var path = RaiseDownload(id, name, total);
        if (string.IsNullOrWhiteSpace(path))
        {
            e.Cancel = true;
            return;
        }
        e.ResultFilePath = path;
        e.Handled = true;
        _downloads[id] = operation;
        operation.StateChanged += (_, _) =>
        {
            switch (operation.State)
            {
                case CoreWebView2DownloadState.Completed:
                    _downloads.Remove(id);
                    OnDownloadFinished(id, 1);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    _downloads.Remove(id);
                    OnDownloadFinished(id, 0);
                    break;
            }
        };
    }

    void FilterRequest(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!WinBackend.AdsOn) return;
        try
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var request)) return;
            if (request.Scheme is not ("http" or "https")) return;
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var page)) return;
            if (!AdBlock.ShouldBlock(request, page.Host, true, WinBackend.Allow)) return;
            e.Response = WinBackend.Env?.CreateWebResourceResponse(null, 403, "Blocked", "");
        }
        catch
        {
            // Never let the filter break a load.
        }
    }

    static bool IsData(string? url) => url is not null && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Child HWND that WebView2 controllers live in, hosted by Avalonia's NativeControlHost.</summary>
static class WinHost
{
    const string ClassName = "CoveWebHost";
    const uint WS_CHILD = 0x40000000;
    const uint WS_VISIBLE = 0x10000000;
    const uint WS_CLIPSIBLINGS = 0x04000000;
    const uint WS_CLIPCHILDREN = 0x02000000;
    const uint WM_SIZE = 0x0005;
    const uint WM_ERASEBKGND = 0x0014;

    static readonly Win32.WndProc Proc = Callback;
    static bool _registered;

    public static event Action<IntPtr>? Resized;

    public static IntPtr Create(IntPtr parent)
    {
        Register();
        var hwnd = Win32.CreateWindowExW(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Couldn't create the web view host window.");
        return hwnd;
    }

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && Win32.IsWindow(hwnd);

    public static void Destroy(IntPtr hwnd)
    {
        if (IsAlive(hwnd)) Win32.DestroyWindow(hwnd);
    }

    static void Register()
    {
        if (_registered) return;
        var cls = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = ClassName
        };
        Win32.RegisterClassExW(ref cls);
        _registered = true;
    }

    static IntPtr Callback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_ERASEBKGND) return (IntPtr)1;
        if (msg == WM_SIZE)
        {
            try { Resized?.Invoke(hwnd); }
            catch (Exception ex) { App.Log(ex.ToString()); }
        }
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

static class Win32
{
    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    // Process snapshot, used to total WebView2's child processes.
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
