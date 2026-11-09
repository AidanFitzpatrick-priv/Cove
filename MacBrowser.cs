using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;

namespace Cove;

/// <summary>WebKit backend state: content-rule compilation and shared native helpers (macOS only).</summary>
static class MacBackend
{
    static int _generation;
    static IntPtr _rules;

    public static IntPtr Rules => _rules;

    public static void Init(string pagesDirectory) => Native.Init(pagesDirectory);
    public static void SetPlugins(string directory) => Native.SetPlugins(directory);
    public static IntPtr CreateContainer() => Native.CreateContainer();
    public static void ReleaseContainer(IntPtr container) => Native.Release(container);
    public static void Fit(IntPtr container) => Native.Fit(container);
    public static IntPtr CreatePrivateStore() => Native.PrivateStore();
    public static void ReleaseStore(IntPtr store) => Native.Release(store);
    public static void SetForceDark(bool enabled) => Native.SetForceDark(enabled ? 1 : 0);

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
        foreach (var page in WebEngine.Snapshot()) page.ApplyRules(_rules);
    }

    sealed class RuleWait(int generation, TaskCompletionSource task)
    {
        public int Generation { get; } = generation;
        public TaskCompletionSource Task { get; } = task;
    }
}

sealed class MacPageSession : PageSession
{
    readonly GCHandle _self;
    IntPtr _page;

    public MacPageSession(bool isPrivate, IntPtr store, IntPtr rules, string script)
    {
        _self = GCHandle.Alloc(this);
        _page = Native.CreatePage(isPrivate ? 1 : 0, store, rules, script, GCHandle.ToIntPtr(_self));
        if (_page == IntPtr.Zero) throw new InvalidOperationException("WebKit couldn't open a page.");
    }

    public override bool CanGoBack => !Dead && Native.CanGoBack(_page) != 0;
    public override bool CanGoForward => !Dead && Native.CanGoForward(_page) != 0;

    public override void Attach(IntPtr container) { if (!Dead) Native.Attach(_page, container); }
    public override void SetVisible(bool visible) { if (!Dead) Native.Show(_page, visible ? 1 : 0); }
    public override void ApplyRules(IntPtr rules) { if (!Dead) Native.SetRules(_page, rules); }
    public override void SetScript(string script) { if (!Dead) Native.SetScript(_page, script); }
    public override void Focus() { if (!Dead) Native.Focus(_page); }
    public override void GoBack() { if (CanGoBack) Native.Back(_page); }
    public override void GoForward() { if (CanGoForward) Native.Forward(_page); }
    public override void Reload() { if (!Dead) Native.Reload(_page); }
    public override void Stop() { if (!Dead) Native.Stop(_page); }
    public override void CancelDownload(int id) { if (!Dead) Native.CancelDownload(_page, id); }

    public override void Navigate(string url)
    {
        if (Dead) return;
        Asleep = false;
        Url = url;
        Native.Navigate(_page, url);
    }

    public override void LoadHtml(string html)
    {
        if (Dead) return;
        Asleep = false;
        Native.LoadHtml(_page, html);
    }

    public override void Sleep()
    {
        if (Dead || Asleep) return;
        ResumeUrl = Url;
        Asleep = true;
        Native.Navigate(_page, "about:blank");
    }

    public override void Wake()
    {
        if (Dead || !Asleep) return;
        Asleep = false;
        if (!string.IsNullOrWhiteSpace(ResumeUrl)) Navigate(ResumeUrl);
    }

    public override void Run(string js)
    {
        if (!Dead) Native.Eval(_page, js, IntPtr.Zero);
    }

    public override Task<string?> EvalJson(string expression)
    {
        if (Dead) return Task.FromResult<string?>(null);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(tcs);
        Native.Eval(_page, EvalWrapper(expression), GCHandle.ToIntPtr(handle));
        return tcs.Task;
    }

    protected override void DisposeCore()
    {
        var page = _page;
        _page = IntPtr.Zero;
        if (page != IntPtr.Zero) Native.Destroy(page);
        if (_self.IsAllocated) _self.Free();
    }

    internal void OnDownload(int id, string name, long total, IntPtr pathOut, int pathCap) =>
        WritePath(pathOut, pathCap, RaiseDownload(id, name, total));

    static void WritePath(IntPtr pathOut, int pathCap, string path)
    {
        if (pathOut == IntPtr.Zero || pathCap <= 1 || string.IsNullOrWhiteSpace(path)) return;
        var bytes = Encoding.UTF8.GetBytes(path);
        var count = Math.Min(bytes.Length, pathCap - 1);
        Marshal.Copy(bytes, 0, pathOut, count);
        Marshal.WriteByte(pathOut, count, 0);
    }
}

static class MacSignIn
{
    public static bool Check(string password) => OperatingSystem.IsMacOS() && Native.LoginCheck(password) != 0;
}

static class MacKeychain
{
    public static void Save(string service, string account, byte[] secret)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The macOS keychain isn't available on this system.");
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
        if (!OperatingSystem.IsMacOS()) return null;
        var ptr = Native.KeychainLoad(service, account, out var length);
        if (ptr == IntPtr.Zero || length <= 0) return null;
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        Native.SecretFree(ptr, length);
        return bytes;
    }

    public static bool Exists(string service, string account) =>
        OperatingSystem.IsMacOS() && Native.KeychainExists(service, account) != 0;
}

static class MacTouchId
{
    public static bool Available() => OperatingSystem.IsMacOS() && Native.TouchIdAvailable() != 0;

    public static Task<bool> EvaluateAsync(string reason)
    {
        if (!OperatingSystem.IsMacOS()) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(tcs);
        Native.TouchIdEvaluate(reason, GCHandle.ToIntPtr(handle), Native.TouchIdThunk);
        return tcs.Task;
    }
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
        cove_set_force_dark(WebScripts.ForceDarkPages ? 1 : 0);
    }

    public static void SetPlugins(string directory) => cove_set_plugins_dir(directory);

    public static IntPtr CreateContainer() => cove_container_create();
    public static void Release(IntPtr obj) { if (obj != IntPtr.Zero) cove_release(obj); }
    public static void Fit(IntPtr container) => cove_surface_fit(container);
    public static IntPtr PrivateStore() => cove_private_store();
    public static void SetForceDark(int enabled) => cove_set_force_dark(enabled);
    public static int LoginCheck(string password) => cove_login_check(password);
    public static int KeychainSave(string service, string account, IntPtr bytes, int length) => cove_keychain_save(service, account, bytes, length);
    public static IntPtr KeychainLoad(string service, string account, out int length) => cove_keychain_load(service, account, out length);
    public static int KeychainExists(string service, string account) => cove_keychain_exists(service, account);
    public static void SecretFree(IntPtr ptr, int length) => cove_secret_free(ptr, length);
    public static int TouchIdAvailable() => cove_touch_id_available();
    public static void TouchIdEvaluate(string reason, IntPtr ctx, TouchIdCb cb) => cove_touch_id_evaluate(reason, ctx, cb);
    public static readonly TouchIdCb TouchIdThunk = OnTouchId;

    static void OnTouchId(IntPtr ctx, int ok)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(ctx);
            if (handle.Target is TaskCompletionSource<bool> tcs)
            {
                handle.Free();
                tcs.TrySetResult(ok != 0);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex.ToString());
        }
    }

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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void TouchIdCb(IntPtr ctx, int ok);

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
        try { MacBackend.RulesReady(ctx, rules, error); }
        catch (Exception ex) { App.Log(ex.ToString()); }
    }

    static MacPageSession? Page(IntPtr ctx)
    {
        try
        {
            return GCHandle.FromIntPtr(ctx).Target as MacPageSession;
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
    [DllImport(Lib)] static extern void cove_set_plugins_dir(string path);
    [DllImport(Lib)] static extern void cove_set_force_dark(int enabled);
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
    [DllImport(Lib)] static extern int cove_touch_id_available();
    [DllImport(Lib)] static extern void cove_touch_id_evaluate(string reason, IntPtr ctx, TouchIdCb cb);
}