using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Cove;

static class WebEngine
{
    static readonly List<PageSession> Pages = [];
    static readonly object Gate = new();
    static int _generation;
    static IntPtr _rules;
    static string _script = WebScripts.Document;
    static List<string> _extensionPaths = [];

    public static string PagesDirectory => Path.Combine(AppContext.BaseDirectory, "pages");
    public static IReadOnlyList<ExtensionPack> ExtensionPacks { get; private set; } = [];

    public static async Task Start(bool block, IReadOnlyCollection<string> allow, IReadOnlyList<string> extensionPaths)
    {
        Native.Init(PagesDirectory);
        _extensionPaths = extensionPaths.Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToList();
        RebuildScript();
        await ApplyBlocking(block, allow);
        App.Log("webkit ready");
    }

    public static PageSession OpenPage(bool isPrivate, IntPtr store)
    {
        var page = new PageSession(isPrivate, store, _rules, _script);
        lock (Gate) Pages.Add(page);
        return page;
    }

    public static void ClosePage(PageSession page)
    {
        lock (Gate) Pages.Remove(page);
    }

    public static void SetExtensions(IReadOnlyList<string> paths)
    {
        _extensionPaths = paths.Where(Directory.Exists).Distinct(StringComparer.Ordinal).ToList();
        RebuildScript();
        foreach (var page in Snapshot()) page.SetScript(_script);
    }

    public static Task ApplyBlocking(bool enabled, IReadOnlyCollection<string> allow)
    {
        var generation = ++_generation;
        if (!enabled)
        {
            SwapRules(generation, IntPtr.Zero);
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new RuleWait(generation, tcs);
        var handle = GCHandle.Alloc(wait);
        Native.CompileRules("cove-" + generation, AdBlock.BuildRules(allow), GCHandle.ToIntPtr(handle));
        return tcs.Task;
    }

    internal static void RulesReady(IntPtr ctx, IntPtr rules, IntPtr error)
    {
        var handle = GCHandle.FromIntPtr(ctx);
        var wait = (RuleWait)handle.Target!;
        handle.Free();
        var message = error == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(error);
        if (!string.IsNullOrWhiteSpace(message)) App.Log("content rules: " + message);
        Dispatcher.UIThread.Post(() =>
        {
            if (rules == IntPtr.Zero || wait.Generation != _generation)
            {
                if (rules != IntPtr.Zero) Native.Release(rules);
                wait.Task.TrySetResult();
                return;
            }
            SwapRules(wait.Generation, rules);
            wait.Task.TrySetResult();
        });
    }

    static void SwapRules(int generation, IntPtr rules)
    {
        if (generation != _generation && rules != IntPtr.Zero)
        {
            Native.Release(rules);
            return;
        }
        var previous = _rules;
        _rules = rules;
        if (previous != IntPtr.Zero && previous != rules) Native.Release(previous);
        foreach (var page in Snapshot()) page.ApplyRules(_rules);
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
        _script = WebScripts.Document + extra;
    }

    static List<PageSession> Snapshot()
    {
        lock (Gate) return Pages.ToList();
    }

    sealed class RuleWait(int generation, TaskCompletionSource task)
    {
        public int Generation { get; } = generation;
        public TaskCompletionSource Task { get; } = task;
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

sealed class PageSession : IDisposable
{
    readonly GCHandle _self;
    IntPtr _page;
    bool _dead;
    bool _asleep;
    string _resume = "";
    string? _lastPopup;
    long _lastPopupAt;

    public PageSession(bool isPrivate, IntPtr store, IntPtr rules, string script)
    {
        _self = GCHandle.Alloc(this);
        _page = Native.CreatePage(isPrivate ? 1 : 0, store, rules, script, GCHandle.ToIntPtr(_self));
        if (_page == IntPtr.Zero) throw new InvalidOperationException("WebKit couldn't open a page.");
    }

    public string Url { get; private set; } = "";
    public bool IsAsleep => _asleep;
    public bool CanGoBack => !_dead && Native.CanGoBack(_page) != 0;
    public bool CanGoForward => !_dead && Native.CanGoForward(_page) != 0;

    public event Action<string>? NavigationStarting;
    public event Action<string, bool>? NavigationCompleted;
    public event Action<string>? TitleChanged;
    public event Action<string, string, bool>? ScriptMessage;
    public event Action<string>? PopupRequested;
    public event Action<DownloadOffer>? DownloadReady;
    public event Action<int, int>? DownloadFinished;
    public event Action? Crashed;

    public void Attach(IntPtr container) { if (!_dead) Native.Attach(_page, container); }
    public void SetVisible(bool visible) { if (!_dead) Native.Show(_page, visible ? 1 : 0); }
    public void ApplyRules(IntPtr rules) { if (!_dead) Native.SetRules(_page, rules); }
    public void SetScript(string script) { if (!_dead) Native.SetScript(_page, script); }
    public void Focus() { if (!_dead) Native.Focus(_page); }
    public void GoBack() { if (CanGoBack) Native.Back(_page); }
    public void GoForward() { if (CanGoForward) Native.Forward(_page); }
    public void Reload() { if (!_dead) Native.Reload(_page); }
    public void Stop() { if (!_dead) Native.Stop(_page); }
    public void CancelDownload(int id) { if (!_dead) Native.CancelDownload(_page, id); }

    public void Navigate(string url)
    {
        if (_dead) return;
        _asleep = false;
        Url = url;
        Native.Navigate(_page, url);
    }

    public void LoadHtml(string html)
    {
        if (_dead) return;
        _asleep = false;
        Native.LoadHtml(_page, html);
    }

    public void Sleep()
    {
        if (_dead || _asleep) return;
        _resume = Url;
        _asleep = true;
        Native.Navigate(_page, "about:blank");
    }

    public void Wake()
    {
        if (_dead || !_asleep) return;
        _asleep = false;
        if (!string.IsNullOrWhiteSpace(_resume)) Navigate(_resume);
    }

    public void Run(string js)
    {
        if (!_dead) Native.Eval(_page, js, IntPtr.Zero);
    }

    public Task<string?> EvalJson(string expression)
    {
        if (_dead) return Task.FromResult<string?>(null);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(tcs);
        var js = "(() => { try { const value = (" + expression + "); if (value && typeof value.then === 'function') return value.then(v => JSON.stringify(v === undefined ? null : v)); return JSON.stringify(value === undefined ? null : value); } catch (e) { return JSON.stringify({ __coveError: String(e && e.message || e) }); } })()";
        Native.Eval(_page, js, GCHandle.ToIntPtr(handle));
        return tcs.Task;
    }

    public async Task<bool> IsPlayingAudioAsync()
    {
        if (_dead || _asleep) return false;
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
        if (_dead) return;
        _dead = true;
        WebEngine.ClosePage(this);
        var page = _page;
        _page = IntPtr.Zero;
        if (page != IntPtr.Zero) Native.Destroy(page);
        if (_self.IsAllocated) _self.Free();
    }

    internal void OnNavigating(string url)
    {
        if (_dead || (_asleep && url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))) return;
        Url = url;
        NavigationStarting?.Invoke(url);
    }

    internal void OnNavigated(string url, bool success)
    {
        if (_dead || _asleep) return;
        if (!string.IsNullOrWhiteSpace(url)) Url = url;
        NavigationCompleted?.Invoke(Url, success);
    }

    internal void OnTitle(string title)
    {
        if (!_dead && !_asleep) TitleChanged?.Invoke(title);
    }

    internal void OnMessage(string json, string source, bool mainFrame)
    {
        if (!_dead) ScriptMessage?.Invoke(json, source, mainFrame);
    }

    internal void OnPopup(string url)
    {
        if (_dead || string.IsNullOrWhiteSpace(url)) return;
        var now = Environment.TickCount64;
        if (url == _lastPopup && now - _lastPopupAt < 800) return;
        _lastPopup = url;
        _lastPopupAt = now;
        PopupRequested?.Invoke(url);
    }

    internal void OnDownload(int id, string name, long total, IntPtr pathOut, int pathCap)
    {
        if (_dead) return;
        var offer = new DownloadOffer { Id = id, Name = string.IsNullOrWhiteSpace(name) ? "download" : name, Total = total };
        DownloadReady?.Invoke(offer);
        WritePath(pathOut, pathCap, offer.Path);
    }

    internal void OnDownloadFinished(int id, int status)
    {
        if (!_dead) DownloadFinished?.Invoke(id, status);
    }

    internal void OnCrashed()
    {
        if (!_dead) Crashed?.Invoke();
    }

    static void WritePath(IntPtr pathOut, int pathCap, string path)
    {
        if (pathOut == IntPtr.Zero || pathCap <= 1 || string.IsNullOrWhiteSpace(path)) return;
        var bytes = Encoding.UTF8.GetBytes(path);
        var count = Math.Min(bytes.Length, pathCap - 1);
        Marshal.Copy(bytes, 0, pathOut, count);
        Marshal.WriteByte(pathOut, count, 0);
    }
}

sealed class WebHostView : NativeControlHost
{
    readonly List<PageSession> _pages = [];
    IntPtr _container;
    PageSession? _active;

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
            Native.Release(_container);
            _container = IntPtr.Zero;
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_container == IntPtr.Zero) _container = Native.CreateContainer();
        foreach (var page in _pages) page.Attach(_container);
        if (_active is not null) _active.SetVisible(true);
        return new PlatformHandle(_container, "NSView");
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
        if (_container != IntPtr.Zero) Native.Fit(_container);
    }
}

static class MacSignIn
{
    public static bool Check(string password) => Native.LoginCheck(password) != 0;
}

static class MacKeychain
{
    public static void Save(string service, string account, byte[] secret)
    {
        var ptr = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, ptr, secret.Length);
            if (Native.KeychainSave(service, account, ptr, secret.Length) == 0)
                throw new InvalidOperationException("Couldn't store the vault key in the login keychain.");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static byte[]? Load(string service, string account)
    {
        var ptr = Native.KeychainLoad(service, account, out var length);
        if (ptr == IntPtr.Zero || length <= 0) return null;
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        Native.SecretFree(ptr, length);
        return bytes;
    }

    public static bool Exists(string service, string account) => Native.KeychainExists(service, account) != 0;
}

static partial class Native
{
    const string Lib = "CoveWebKit";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
        {
            if (name != Lib) return IntPtr.Zero;
            return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libCoveWebKit.dylib"));
        });
        Callbacks = new PageCallbacks
        {
            Navigating = Fn(OnNavigating),
            Navigated = Fn(OnNavigated),
            Title = Fn(OnTitle),
            Message = Fn(OnMessage),
            Popup = Fn(OnPopup),
            Download = Fn(OnDownload),
            DownloadFinished = Fn(OnDownloadFinished),
            Crashed = Fn(OnCrashed)
        };
        EvalThunk = OnEval;
        RuleThunk = OnRules;
    }

    static PageCallbacks Callbacks;
    static readonly EvalCb EvalThunk;
    static readonly RuleCb RuleThunk;

    public static void Init(string pagesDirectory)
    {
        cove_init();
        cove_set_pages_dir(pagesDirectory);
    }

    public static IntPtr CreateContainer() => cove_container_create();
    public static void Release(IntPtr obj) { if (obj != IntPtr.Zero) cove_release(obj); }
    public static void Fit(IntPtr container) => cove_surface_fit(container);
    public static IntPtr PrivateStore() => cove_private_store();
    public static int LoginCheck(string password) => cove_login_check(password);
    public static int KeychainSave(string service, string account, IntPtr bytes, int length) => cove_keychain_save(service, account, bytes, length);
    public static IntPtr KeychainLoad(string service, string account, out int length) => cove_keychain_load(service, account, out length);
    public static int KeychainExists(string service, string account) => cove_keychain_exists(service, account);
    public static void SecretFree(IntPtr ptr, int length) => cove_secret_free(ptr, length);

    public static void CompileRules(string identifier, string json, IntPtr ctx) =>
        cove_compile_rules(identifier, json, ctx, RuleThunk);

    public static IntPtr CreatePage(int isPrivate, IntPtr store, IntPtr rules, string script, IntPtr ctx) =>
        cove_page_create(isPrivate, store, rules, script, ctx, ref Callbacks);

    public static void Destroy(IntPtr page) => cove_page_destroy(page);
    public static void Attach(IntPtr page, IntPtr container) => cove_page_attach(page, container);
    public static void Show(IntPtr page, int visible) => cove_page_show(page, visible);
    public static void Navigate(IntPtr page, string url) => cove_page_navigate(page, url);
    public static void LoadHtml(IntPtr page, string html) => cove_page_load_html(page, html);
    public static void Back(IntPtr page) => cove_page_back(page);
    public static void Forward(IntPtr page) => cove_page_forward(page);
    public static void Reload(IntPtr page) => cove_page_reload(page);
    public static void Stop(IntPtr page) => cove_page_stop(page);
    public static void Focus(IntPtr page) => cove_page_focus(page);
    public static int CanGoBack(IntPtr page) => cove_page_can_back(page);
    public static int CanGoForward(IntPtr page) => cove_page_can_forward(page);
    public static void SetRules(IntPtr page, IntPtr rules) => cove_page_set_rules(page, rules);
    public static void SetScript(IntPtr page, string script) => cove_page_set_script(page, script);
    public static void CancelDownload(IntPtr page, int id) => cove_download_cancel(page, id);
    public static void Eval(IntPtr page, string js, IntPtr ctx) => cove_page_eval(page, js, ctx, ctx == IntPtr.Zero ? null : EvalThunk);

    static IntPtr Fn(Delegate del) => Marshal.GetFunctionPointerForDelegate(del);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UrlCb(IntPtr ctx, IntPtr url);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void NavDoneCb(IntPtr ctx, IntPtr url, int success);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void TitleCb(IntPtr ctx, IntPtr title);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void MessageCb(IntPtr ctx, IntPtr json, IntPtr source, int mainFrame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PopupCb(IntPtr ctx, IntPtr url);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DownloadCb(IntPtr ctx, int ident, IntPtr name, long bytes, IntPtr pathOut, int pathCap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DownloadDoneCb(IntPtr ctx, int ident, int success);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CrashCb(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void EvalCb(IntPtr ctx, IntPtr result, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RuleCb(IntPtr ctx, IntPtr rules, IntPtr error);

    static readonly UrlCb OnNavigating = (ctx, url) => Page(ctx)?.OnNavigating(Text(url));
    static readonly NavDoneCb OnNavigated = (ctx, url, success) => Page(ctx)?.OnNavigated(Text(url), success != 0);
    static readonly TitleCb OnTitle = (ctx, title) => Page(ctx)?.OnTitle(Text(title));
    static readonly MessageCb OnMessage = (ctx, json, source, main) => Page(ctx)?.OnMessage(Text(json), Text(source), main != 0);
    static readonly PopupCb OnPopup = (ctx, url) => Page(ctx)?.OnPopup(Text(url));
    static readonly DownloadCb OnDownload = (ctx, ident, name, bytes, path, cap) => Page(ctx)?.OnDownload(ident, Text(name), bytes, path, cap);
    static readonly DownloadDoneCb OnDownloadFinished = (ctx, ident, status) => Page(ctx)?.OnDownloadFinished(ident, status);
    static readonly CrashCb OnCrashed = ctx => Page(ctx)?.OnCrashed();

    static void OnEval(IntPtr ctx, IntPtr result, IntPtr error)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(ctx);
            if (handle.Target is TaskCompletionSource<string?> tcs)
            {
                handle.Free();
                tcs.TrySetResult(result == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(result));
            }
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
        }
    }

    static void OnRules(IntPtr ctx, IntPtr rules, IntPtr error)
    {
        try { WebEngine.RulesReady(ctx, rules, error); }
        catch (Exception ex) { App.Log(ex.ToString()); }
    }

    static PageSession? Page(IntPtr ctx)
    {
        try
        {
            return GCHandle.FromIntPtr(ctx).Target as PageSession;
        }
        catch
        {
            return null;
        }
    }

    static string Text(IntPtr ptr) => ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";

    [StructLayout(LayoutKind.Sequential)]
    struct PageCallbacks
    {
        public IntPtr Navigating;
        public IntPtr Navigated;
        public IntPtr Title;
        public IntPtr Message;
        public IntPtr Popup;
        public IntPtr Download;
        public IntPtr DownloadFinished;
        public IntPtr Crashed;
    }

    [DllImport(Lib)] static extern void cove_init();
    [DllImport(Lib)] static extern void cove_set_pages_dir(string path);
    [DllImport(Lib)] static extern IntPtr cove_container_create();
    [DllImport(Lib)] static extern void cove_release(IntPtr obj);
    [DllImport(Lib)] static extern void cove_surface_fit(IntPtr container);
    [DllImport(Lib)] static extern IntPtr cove_private_store();
    [DllImport(Lib)] static extern void cove_compile_rules(string identifier, string json, IntPtr ctx, RuleCb? cb);
    [DllImport(Lib)] static extern IntPtr cove_page_create(int isPrivate, IntPtr store, IntPtr rules, string script, IntPtr ctx, ref PageCallbacks callbacks);
    [DllImport(Lib)] static extern void cove_page_destroy(IntPtr page);
    [DllImport(Lib)] static extern void cove_page_attach(IntPtr page, IntPtr container);
    [DllImport(Lib)] static extern void cove_page_show(IntPtr page, int visible);
    [DllImport(Lib)] static extern void cove_page_navigate(IntPtr page, string url);
    [DllImport(Lib)] static extern void cove_page_load_html(IntPtr page, string html);
    [DllImport(Lib)] static extern void cove_page_back(IntPtr page);
    [DllImport(Lib)] static extern void cove_page_forward(IntPtr page);
    [DllImport(Lib)] static extern void cove_page_reload(IntPtr page);
    [DllImport(Lib)] static extern void cove_page_stop(IntPtr page);
    [DllImport(Lib)] static extern void cove_page_focus(IntPtr page);
    [DllImport(Lib)] static extern int cove_page_can_back(IntPtr page);
    [DllImport(Lib)] static extern int cove_page_can_forward(IntPtr page);
    [DllImport(Lib)] static extern void cove_page_eval(IntPtr page, string js, IntPtr ctx, EvalCb? cb);
    [DllImport(Lib)] static extern void cove_page_set_rules(IntPtr page, IntPtr rules);
    [DllImport(Lib)] static extern void cove_page_set_script(IntPtr page, string script);
    [DllImport(Lib)] static extern void cove_download_cancel(IntPtr page, int ident);
    [DllImport(Lib)] static extern int cove_login_check(string password);
    [DllImport(Lib)] static extern int cove_keychain_save(string service, string account, IntPtr bytes, int length);
    [DllImport(Lib)] static extern IntPtr cove_keychain_load(string service, string account, out int length);
    [DllImport(Lib)] static extern int cove_keychain_exists(string service, string account);
    [DllImport(Lib)] static extern void cove_secret_free(IntPtr ptr, int length);
}
