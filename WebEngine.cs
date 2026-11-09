using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Cove;

/// <summary>
/// Shared browser orchestration. Platform work lives in MacBrowser.cs (WebKit) and WinBrowser.cs (WebView2).
/// </summary>
static class WebEngine
{
    static readonly List<PageSession> Pages = [];
    static readonly object Gate = new();
    static string _script = WebScripts.Document;
    static List<string> _extensionPaths = [];

    public static string PagesDirectory => Path.Combine(AppContext.BaseDirectory, "pages");
    public static IReadOnlyList<ExtensionPack> ExtensionPacks { get; private set; } = [];

    public static async Task Start(bool block, IReadOnlyCollection<string> allow, IReadOnlyList<string> extensionPaths)
    {
        _extensionPaths = extensionPaths.Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToList();
        RebuildScript();
        if (OperatingSystem.IsWindows()) await StartWindows(block, allow);
        else await StartMac(block, allow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task StartWindows(bool block, IReadOnlyCollection<string> allow)
    {
        await WinBackend.Start(block, allow);
        App.Log("webview2 ready");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task StartMac(bool block, IReadOnlyCollection<string> allow)
    {
        MacBackend.Init(PagesDirectory);
        await MacBackend.ApplyBlocking(block, allow);
        App.Log("webkit ready");
    }

    public static PageSession OpenPage(bool isPrivate, IntPtr store)
    {
        var page = OperatingSystem.IsWindows() ? OpenWindows(isPrivate, store) : OpenMac(isPrivate, store);
        lock (Gate) Pages.Add(page);
        return page;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static PageSession OpenWindows(bool isPrivate, IntPtr store) => new WinPageSession(isPrivate, store, _script);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static PageSession OpenMac(bool isPrivate, IntPtr store) => new MacPageSession(isPrivate, store, MacBackend.Rules, _script);

    public static void ClosePage(PageSession page)
    {
        lock (Gate) Pages.Remove(page);
    }

    /// <summary>Opaque per-window store for a private window. Release it with <see cref="ReleaseStore"/>.</summary>
    public static IntPtr CreatePrivateStore() =>
        OperatingSystem.IsWindows() ? WinBackend.CreatePrivateStore() : MacBackend.CreatePrivateStore();

    public static void ReleaseStore(IntPtr store)
    {
        if (store == IntPtr.Zero) return;
        if (OperatingSystem.IsWindows()) WinBackend.ReleaseStore(store);
        else MacBackend.ReleaseStore(store);
    }

    public static void SetExtensions(IReadOnlyList<string> paths)
    {
        _extensionPaths = paths.Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToList();
        RebuildScript();
        foreach (var page in Snapshot()) page.SetScript(_script);
    }

    public static void SetForceDarkPages(bool enabled)
    {
        WebScripts.ForceDarkPages = enabled;
        RebuildScript();
        foreach (var page in Snapshot()) page.SetScript(_script);
        if (OperatingSystem.IsWindows()) ApplyPreferredSchemeWindows();
    }

    public static void ApplyPreferredColorScheme()
    {
        if (OperatingSystem.IsWindows()) ApplyPreferredSchemeWindows();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ApplyPreferredSchemeWindows() => WinBackend.ApplyPreferredColorScheme();

    public static Task ApplyBlocking(bool enabled, IReadOnlyCollection<string> allow)
    {
        if (OperatingSystem.IsWindows()) return ApplyBlockingWindows(enabled, allow);
        return MacBackend.ApplyBlocking(enabled, allow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Task ApplyBlockingWindows(bool enabled, IReadOnlyCollection<string> allow)
    {
        WinBackend.SetBlocking(enabled, allow);
        return Task.CompletedTask;
    }

    static void RebuildScript()
    {
        var packs = new List<ExtensionPack>();
        var extra = new StringBuilder();
        foreach (var folder in _extensionPaths)
        {
            try
            {
                var pack = ExtensionPack.Read(folder);
                if (pack is null) continue;
                packs.Add(pack);
                extra.Append('\n').Append(pack.Script);
            }
            catch (Exception ex)
            {
                App.Log("extension " + folder + ": " + ex.Message);
            }
        }
        ExtensionPacks = packs;
        _script = WebScripts.BuildDocument(extra.ToString());
    }

    internal static List<PageSession> Snapshot()
    {
        lock (Gate) return Pages.ToList();
    }
}

sealed class ExtensionPack
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool HasScripts { get; init; }
    public string Script { get; init; } = "";

    public static ExtensionPack? Read(string folder)
    {
        var manifestPath = System.IO.Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileName(folder);
        var script = new StringBuilder();
        var has = false;
        if (root.TryGetProperty("content_scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in scripts.EnumerateArray())
            {
                var patterns = ReadList(item, "matches");
                var atEnd = item.TryGetProperty("run_at", out var run) && run.GetString() is "document_end" or "document_idle";
                foreach (var file in ReadList(item, "js"))
                    script.Append(Wrap(folder, file, patterns, atEnd, css: false));
                foreach (var file in ReadList(item, "css"))
                    script.Append(Wrap(folder, file, patterns, atEnd, css: true));
                if (patterns.Count > 0) has = true;
            }
        }
        return new ExtensionPack { Name = name, Path = folder, HasScripts = has, Script = script.ToString() };
    }

    static List<string> ReadList(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Select(entry => entry.GetString() ?? "").Where(text => text.Length > 0).ToList();
    }

    static string Wrap(string folder, string relative, List<string> patterns, bool atEnd, bool css)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var root = System.IO.Path.GetFullPath(folder);
        var rootPrefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(full)) return "";
        var payload = JsonSerializer.Serialize(new ExtensionPayload(patterns, File.ReadAllText(full), atEnd, css), Camel);
        return "(() => { const spec = " + payload + "; if (window.__coveExtRun) window.__coveExtRun(spec); })();\n";
    }

    static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    sealed record ExtensionPayload(List<string> Patterns, string Code, bool AtEnd, bool Css);
}

sealed class DownloadOffer
{
    public int Id { get; init; }
    public string Name { get; init; } = "download";
    public long Total { get; init; }
    public string Path { get; set; } = "";
}
/// <summary>One browser tab. Implemented by <c>MacPageSession</c> (WebKit) and <c>WinPageSession</c> (WebView2).</summary>
abstract class PageSession : IDisposable
{
    protected bool Dead;
    protected bool Asleep;
    protected string ResumeUrl = "";
    string? _lastPopup;
    long _lastPopupAt;

    public string Url { get; protected set; } = "";
    public bool IsAsleep => Asleep;
    public abstract bool CanGoBack { get; }
    public abstract bool CanGoForward { get; }

    public event Action<string>? NavigationStarting;
    public event Action<string, bool>? NavigationCompleted;
    public event Action<string>? TitleChanged;
    public event Action<string, string, bool>? ScriptMessage;
    public event Action<string>? PopupRequested;
    public event Action<DownloadOffer>? DownloadReady;
    public event Action<int, int>? DownloadFinished;
    public event Action? Crashed;

    /// <summary>Parent surface: an NSView on macOS, an HWND on Windows.</summary>
    public abstract void Attach(IntPtr container);
    public abstract void SetVisible(bool visible);
    /// <summary>WebKit content-rule list (macOS only). Ignored on Windows, which filters requests directly.</summary>
    public virtual void ApplyRules(IntPtr rules) { }
    public abstract void SetScript(string script);
    public abstract void Focus();
    public abstract void GoBack();
    public abstract void GoForward();
    public abstract void Reload();
    public abstract void Stop();
    public abstract void CancelDownload(int id);
    public abstract void Navigate(string url);
    public abstract void LoadHtml(string html);
    public abstract void Sleep();
    public abstract void Wake();
    public abstract void Run(string js);
    public abstract Task<string?> EvalJson(string expression);

    /// <summary>Delivers a JSON message to the page's chrome.webview "message" listeners.</summary>
    public virtual void PostJson(string json) =>
        Run("window.__coveDeliver(" + JsonSerializer.Serialize(json) + ")");

    /// <summary>Re-fits the page to its host surface.</summary>
    internal virtual void Layout() { }

    public async Task<bool> IsPlayingAudioAsync()
    {
        if (Dead || Asleep) return false;
        try
        {
            var json = await EvalJson("!![...document.querySelectorAll('video,audio')].some(m => !m.paused && !m.ended && m.currentTime > 0)");
            return json == "true";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Dead) return;
        Dead = true;
        WebEngine.ClosePage(this);
        DisposeCore();
    }

    protected abstract void DisposeCore();

    protected static string EvalWrapper(string expression) =>
        "(() => { try { const value = (" + expression + "); if (value && typeof value.then === 'function') return value.then(v => JSON.stringify(v === undefined ? null : v)); return JSON.stringify(value === undefined ? null : value); } catch (e) { return JSON.stringify({ __coveError: String(e && e.message || e) }); } })()";

    internal void OnNavigating(string url)
    {
        if (Dead || (Asleep && url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))) return;
        Url = url;
        NavigationStarting?.Invoke(url);
    }

    internal void OnNavigated(string url, bool success)
    {
        if (Dead || Asleep) return;
        if (!string.IsNullOrWhiteSpace(url)) Url = url;
        NavigationCompleted?.Invoke(Url, success);
    }

    internal void OnTitle(string title)
    {
        if (!Dead && !Asleep) TitleChanged?.Invoke(title);
    }

    internal void OnMessage(string json, string source, bool mainFrame)
    {
        if (!Dead) ScriptMessage?.Invoke(json, source, mainFrame);
    }

    internal void OnPopup(string url)
    {
        if (Dead || string.IsNullOrWhiteSpace(url)) return;
        var now = Environment.TickCount64;
        if (url == _lastPopup && now - _lastPopupAt < 800) return;
        _lastPopup = url;
        _lastPopupAt = now;
        PopupRequested?.Invoke(url);
    }

    /// <summary>Raises DownloadReady and returns the path the handler chose (empty when none).</summary>
    internal string RaiseDownload(int id, string name, long total)
    {
        if (Dead) return "";
        var offer = new DownloadOffer { Id = id, Name = string.IsNullOrWhiteSpace(name) ? "download" : name, Total = total };
        DownloadReady?.Invoke(offer);
        return offer.Path;
    }

    internal void OnDownloadFinished(int id, int status)
    {
        if (!Dead) DownloadFinished?.Invoke(id, status);
    }

    internal void OnCrashed()
    {
        if (!Dead) Crashed?.Invoke();
    }
}

sealed class WebHostView : NativeControlHost
{
    readonly List<PageSession> _pages = [];
    IntPtr _container;
    PageSession? _active;
    bool _hooked;

    public void Adopt(PageSession page)
    {
        if (!_pages.Contains(page)) _pages.Add(page);
        if (_container != IntPtr.Zero) page.Attach(_container);
    }

    public void Activate(PageSession page)
    {
        _active = page;
        foreach (var item in _pages) item.SetVisible(ReferenceEquals(item, page));
        IsVisible = true;
        Fit();
        Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
    }

    public void Drop(PageSession page)
    {
        _pages.Remove(page);
        if (ReferenceEquals(_active, page)) _active = null;
    }

    public void Shutdown()
    {
        foreach (var page in _pages.ToList())
        {
            _pages.Remove(page);
            page.Dispose();
        }
        _active = null;
        if (_container != IntPtr.Zero)
        {
            if (OperatingSystem.IsWindows()) DestroyWindowsContainer();
            else if (OperatingSystem.IsMacOS()) ReleaseMacContainer();
            _container = IntPtr.Zero;
        }
        if (_hooked)
        {
            if (OperatingSystem.IsWindows()) UnhookWindows();
            _hooked = false;
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle;
        if (OperatingSystem.IsWindows()) handle = CreateWindowsContainer(parent);
        else if (OperatingSystem.IsMacOS()) handle = CreateMacContainer();
        else return base.CreateNativeControlCore(parent);
        foreach (var page in _pages) page.Attach(_container);
        if (_active is not null) _active.SetVisible(true);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        foreach (var page in _pages) page.SetVisible(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) Fit();
    }

    void Fit()
    {
        if (_container == IntPtr.Zero) return;
        if (OperatingSystem.IsMacOS()) FitMac();
        foreach (var page in _pages) page.Layout();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    IPlatformHandle CreateMacContainer()
    {
        if (_container == IntPtr.Zero) _container = MacBackend.CreateContainer();
        return new PlatformHandle(_container, "NSView");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void ReleaseMacContainer() => MacBackend.ReleaseContainer(_container);

    [MethodImpl(MethodImplOptions.NoInlining)]
    void FitMac() => MacBackend.Fit(_container);

    [MethodImpl(MethodImplOptions.NoInlining)]
    IPlatformHandle CreateWindowsContainer(IPlatformHandle parent)
    {
        // Avalonia may tear the host window down when the control leaves the visual tree. If it did,
        // make a fresh one; the pages re-parent their controllers in CreateNativeControlCore.
        if (_container != IntPtr.Zero && !WinHost.IsAlive(_container)) _container = IntPtr.Zero;
        if (_container == IntPtr.Zero) _container = WinHost.Create(parent.Handle);
        if (!_hooked)
        {
            WinHost.Resized += OnWindowsResized;
            _hooked = true;
        }
        return new PlatformHandle(_container, "HWND");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void DestroyWindowsContainer() => WinHost.Destroy(_container);

    [MethodImpl(MethodImplOptions.NoInlining)]
    void UnhookWindows() => WinHost.Resized -= OnWindowsResized;

    void OnWindowsResized(IntPtr hwnd)
    {
        if (hwnd == _container) Fit();
    }
}
