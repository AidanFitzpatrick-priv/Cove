#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>
#import <OpenDirectory/OpenDirectory.h>
#import <Security/Security.h>
#import <LocalAuthentication/LocalAuthentication.h>
#import <objc/runtime.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct CovePageCallbacks {
    void (*navigating)(void *ctx, const char *url);
    void (*navigated)(void *ctx, const char *url, int success);
    void (*title)(void *ctx, const char *title);
    void (*message)(void *ctx, const char *json, const char *source, int main_frame);
    void (*popup)(void *ctx, const char *url);
    void (*download)(void *ctx, int ident, const char *name, long long bytes, char *path_out, int path_cap);
    void (*download_finished)(void *ctx, int ident, int success);
    void (*crashed)(void *ctx);
} CovePageCallbacks;

static NSString *PagesDir;
static const void *CoveDownloadKey = &CoveDownloadKey;

static void OnMain(void (^block)(void)) {
    if ([NSThread isMainThread]) block();
    else dispatch_sync(dispatch_get_main_queue(), block);
}

static char *CopyUTF8(NSString *text) {
    const char *utf = text.UTF8String;
    return utf ? strdup(utf) : NULL;
}

@interface CovePageBox : NSObject
@property (nonatomic, strong) WKWebView *webView;
@property (nonatomic, strong) id delegate;
@property (nonatomic, strong) NSMutableDictionary<NSNumber *, WKDownload *> *downloads;
@property (nonatomic, copy) NSString *script;
@property (nonatomic, assign) void *ctx;
@property (nonatomic, assign) CovePageCallbacks cbs;
@property (nonatomic, weak) NSView *container;
@property (nonatomic, assign) BOOL wantsVisible;
@property (nonatomic, assign) int nextDownload;
@property (nonatomic, assign) BOOL observingTitle;
- (void)show:(BOOL)visible;
@end

@interface CoveContainer : NSView
@end

@implementation CoveContainer
- (void)resizeSubviewsWithOldSize:(NSSize)oldSize {
    for (NSView *sub in self.subviews)
        sub.frame = self.bounds;
}
@end

static NSWindow *ParkWindow(void) {
    static NSWindow *window;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 1100, 800)
                                              styleMask:NSWindowStyleMaskBorderless
                                                backing:NSBackingStoreBuffered
                                                  defer:NO];
        window.releasedWhenClosed = NO;
        window.opaque = NO;
        window.backgroundColor = NSColor.clearColor;
        window.ignoresMouseEvents = YES;
        window.collectionBehavior = NSWindowCollectionBehaviorTransient | NSWindowCollectionBehaviorIgnoresCycle;
    });
    return window;
}

@interface CoveDelegate : NSObject <WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler, WKDownloadDelegate, WKURLSchemeHandler>
@property (nonatomic, weak) CovePageBox *page;
@end

@implementation CovePageBox
- (void)show:(BOOL)visible {
    self.wantsVisible = visible;
    NSView *host = nil;
    if (visible) {
        if (!self.container) return;
        host = self.container;
    } else {
        host = ParkWindow().contentView;
    }
    if (!host || !self.webView) return;
    if (self.webView.superview != host) {
        [self.webView removeFromSuperview];
        [host addSubview:self.webView];
    }
    self.webView.hidden = NO;
    NSRect bounds = host.bounds;
    if (NSWidth(bounds) < 2 || NSHeight(bounds) < 2)
        bounds = NSMakeRect(0, 0, 1100, 800);
    self.webView.frame = bounds;
    self.webView.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
}
@end

@implementation CoveDelegate

- (void)webView:(WKWebView *)webView decidePolicyForNavigationAction:(WKNavigationAction *)navigationAction decisionHandler:(void (^)(WKNavigationActionPolicy))decisionHandler {
    NSURL *url = navigationAction.request.URL;
    NSString *scheme = url.scheme.lowercaseString;
    BOOL allowed = [scheme isEqualToString:@"http"] || [scheme isEqualToString:@"https"] || [scheme isEqualToString:@"cove"] || [scheme isEqualToString:@"about"] || [scheme isEqualToString:@"blob"] || [scheme isEqualToString:@"data"];
    if (!allowed || !url) {
        decisionHandler(WKNavigationActionPolicyCancel);
        return;
    }
    if (navigationAction.shouldPerformDownload) {
        decisionHandler(WKNavigationActionPolicyDownload);
        return;
    }
    if (navigationAction.targetFrame.isMainFrame && self.page.cbs.navigating)
        self.page.cbs.navigating(self.page.ctx, url.absoluteString.UTF8String ?: "");
    decisionHandler(WKNavigationActionPolicyAllow);
}

- (void)webView:(WKWebView *)webView decidePolicyForNavigationResponse:(WKNavigationResponse *)navigationResponse decisionHandler:(void (^)(WKNavigationResponsePolicy))decisionHandler {
    if (!navigationResponse.canShowMIMEType) decisionHandler(WKNavigationResponsePolicyDownload);
    else decisionHandler(WKNavigationResponsePolicyAllow);
}

- (void)webView:(WKWebView *)webView didFinishNavigation:(WKNavigation *)navigation {
    if (!self.page.cbs.navigated) return;
    self.page.cbs.navigated(self.page.ctx, webView.URL.absoluteString.UTF8String ?: "", 1);
}

- (void)webView:(WKWebView *)webView didFailProvisionalNavigation:(WKNavigation *)navigation withError:(NSError *)error {
    [self failed:webView error:error];
}

- (void)webView:(WKWebView *)webView didFailNavigation:(WKNavigation *)navigation withError:(NSError *)error {
    [self failed:webView error:error];
}

- (void)failed:(WKWebView *)webView error:(NSError *)error {
    if ([error.domain isEqualToString:NSURLErrorDomain] && error.code == NSURLErrorCancelled) return;
    if (!self.page.cbs.navigated) return;
    self.page.cbs.navigated(self.page.ctx, webView.URL.absoluteString.UTF8String ?: "", 0);
}

- (void)webViewWebContentProcessDidTerminate:(WKWebView *)webView {
    if (self.page.cbs.crashed) self.page.cbs.crashed(self.page.ctx);
}

- (WKWebView *)webView:(WKWebView *)webView createWebViewWithConfiguration:(WKWebViewConfiguration *)configuration forNavigationAction:(WKNavigationAction *)navigationAction windowFeatures:(WKWindowFeatures *)windowFeatures {
    NSURL *url = navigationAction.request.URL;
    if (url && self.page.cbs.popup)
        self.page.cbs.popup(self.page.ctx, url.absoluteString.UTF8String ?: "");
    return nil;
}

- (void)observeValueForKeyPath:(NSString *)keyPath ofObject:(id)object change:(NSDictionary *)change context:(void *)context {
    if ([keyPath isEqualToString:@"title"] && object == self.page.webView) {
        if (self.page.cbs.title)
            self.page.cbs.title(self.page.ctx, self.page.webView.title.UTF8String ?: "");
    }
}

- (void)userContentController:(WKUserContentController *)userContentController didReceiveScriptMessage:(WKScriptMessage *)message {
    if (!self.page.cbs.message) return;
    id body = message.body;
    NSString *json = [body isKindOfClass:[NSString class]] ? body : nil;
    if (!json && body && [NSJSONSerialization isValidJSONObject:body]) {
        NSData *data = [NSJSONSerialization dataWithJSONObject:body options:0 error:nil];
        json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
    }
    if (!json) return;
    NSString *source = message.frameInfo.request.URL.absoluteString ?: @"";
    int mainFrame = message.frameInfo.isMainFrame ? 1 : 0;
    self.page.cbs.message(self.page.ctx, json.UTF8String, source.UTF8String, mainFrame);
}

- (void)rememberDownload:(WKDownload *)download {
    download.delegate = self;
    int ident = ++self.page.nextDownload;
    if (!self.page.downloads) self.page.downloads = [NSMutableDictionary dictionary];
    self.page.downloads[@(ident)] = download;
    objc_setAssociatedObject(download, CoveDownloadKey, @(ident), OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}

- (void)webView:(WKWebView *)webView navigationAction:(WKNavigationAction *)navigationAction didBecomeDownload:(WKDownload *)download {
    [self rememberDownload:download];
}

- (void)webView:(WKWebView *)webView navigationResponse:(WKNavigationResponse *)navigationResponse didBecomeDownload:(WKDownload *)download {
    [self rememberDownload:download];
}

- (int)identForDownload:(WKDownload *)download {
    NSNumber *ident = objc_getAssociatedObject(download, CoveDownloadKey);
    return ident ? ident.intValue : 0;
}

- (void)download:(WKDownload *)download decideDestinationUsingResponse:(NSURLResponse *)response suggestedFilename:(NSString *)suggestedFilename completionHandler:(void (^)(NSURL * _Nullable))completionHandler {
    NSString *name = suggestedFilename.length ? suggestedFilename : response.suggestedFilename;
    if (!name.length) name = @"download";
    char path[4096];
    path[0] = 0;
    if (self.page.cbs.download)
        self.page.cbs.download(self.page.ctx, [self identForDownload:download], name.UTF8String, response.expectedContentLength, path, (int)sizeof(path));
    if (path[0] == 0) {
        completionHandler(nil);
        return;
    }
    NSString *dest = [NSString stringWithUTF8String:path];
    completionHandler(dest ? [NSURL fileURLWithPath:dest] : nil);
}

- (void)downloadDidFinish:(WKDownload *)download {
    int ident = [self identForDownload:download];
    [self.page.downloads removeObjectForKey:@(ident)];
    if (self.page.cbs.download_finished) self.page.cbs.download_finished(self.page.ctx, ident, 1);
}

- (void)download:(WKDownload *)download didFailWithError:(NSError *)error resumeData:(NSData *)resumeData {
    int ident = [self identForDownload:download];
    [self.page.downloads removeObjectForKey:@(ident)];
    int status = ([error.domain isEqualToString:NSURLErrorDomain] && error.code == NSURLErrorCancelled) ? 2 : 0;
    if (self.page.cbs.download_finished) self.page.cbs.download_finished(self.page.ctx, ident, status);
}

- (NSString *)mimeForPath:(NSString *)path encoding:(NSString **)encoding {
    NSString *ext = path.pathExtension.lowercaseString;
    *encoding = nil;
    if ([ext isEqualToString:@"html"] || [ext isEqualToString:@"htm"]) { *encoding = @"utf-8"; return @"text/html"; }
    if ([ext isEqualToString:@"css"]) { *encoding = @"utf-8"; return @"text/css"; }
    if ([ext isEqualToString:@"js"]) { *encoding = @"utf-8"; return @"text/javascript"; }
    if ([ext isEqualToString:@"svg"]) { *encoding = @"utf-8"; return @"image/svg+xml"; }
    if ([ext isEqualToString:@"json"]) { *encoding = @"utf-8"; return @"application/json"; }
    if ([ext isEqualToString:@"png"]) return @"image/png";
    if ([ext isEqualToString:@"jpg"] || [ext isEqualToString:@"jpeg"]) return @"image/jpeg";
    if ([ext isEqualToString:@"ico"]) return @"image/x-icon";
    if ([ext isEqualToString:@"woff2"]) return @"font/woff2";
    *encoding = @"utf-8";
    return @"application/octet-stream";
}

- (void)webView:(WKWebView *)webView startURLSchemeTask:(id<WKURLSchemeTask>)task {
    @autoreleasepool {
        NSURL *url = task.request.URL;
        NSString *host = url.host.lowercaseString ?: @"";
        NSString *rel = url.path ?: @"";
        if ([rel hasPrefix:@"/"]) rel = [rel substringFromIndex:1];
        rel = rel.stringByRemovingPercentEncoding ?: rel;
        NSData *body = nil;
        NSString *encoding = nil;
        NSString *mime = @"text/html";
        BOOL bad = host.length > 0 && ![host isEqualToString:@"cove.local"];
        bad = bad || [rel containsString:@".."] || [rel hasPrefix:@"/"];
        if (!bad && PagesDir.length) {
            NSString *root = PagesDir.stringByStandardizingPath;
            NSString *full = [root stringByAppendingPathComponent:rel].stringByStandardizingPath;
            NSString *prefix = [root hasSuffix:@"/"] ? root : [root stringByAppendingString:@"/"];
            if ([full hasPrefix:prefix] && [NSFileManager.defaultManager fileExistsAtPath:full]) {
                body = [NSData dataWithContentsOfFile:full];
                mime = [self mimeForPath:full encoding:&encoding];
            }
        }
        if (!body) {
            const char *html = "<!DOCTYPE html><meta charset=utf-8><title>Cove</title><body style=\"background:#121316;color:#f2f0ec;font:14px -apple-system\">That page isn't in Cove.</body>";
            body = [NSData dataWithBytes:html length:strlen(html)];
            encoding = @"utf-8";
            mime = @"text/html";
        }
        NSURLResponse *response = [[NSURLResponse alloc] initWithURL:url MIMEType:mime expectedContentLength:(NSInteger)body.length textEncodingName:encoding];
        @try {
            [task didReceiveResponse:response];
            [task didReceiveData:body];
            [task didFinish];
        } @catch (__unused NSException *ex) {
        }
    }
}

- (void)webView:(WKWebView *)webView stopURLSchemeTask:(id<WKURLSchemeTask>)task {
}

@end

static void InstallScript(CovePageBox *page, NSString *source) {
    WKUserContentController *controller = page.webView.configuration.userContentController;
    [controller removeAllUserScripts];
    if (!source.length) return;
    WKUserScript *script = [[WKUserScript alloc] initWithSource:source injectionTime:WKUserScriptInjectionTimeAtDocumentStart forMainFrameOnly:NO];
    [controller addUserScript:script];
}

void cove_init(void) {
    OnMain(^{
        [NSApplication sharedApplication];
    });
}

void cove_set_pages_dir(const char *path) {
    PagesDir = path ? [NSString stringWithUTF8String:path] : nil;
}

void *cove_container_create(void) {
    __block void *result = NULL;
    OnMain(^{
        CoveContainer *view = [[CoveContainer alloc] initWithFrame:NSMakeRect(0, 0, 800, 600)];
        view.autoresizesSubviews = YES;
        view.wantsLayer = YES;
        view.layer.backgroundColor = [[NSColor colorWithSRGBRed:0x12 / 255.0 green:0x13 / 255.0 blue:0x16 / 255.0 alpha:1] CGColor];
        result = (__bridge_retained void *)view;
    });
    return result;
}

void cove_release(void *obj) {
    if (obj) CFRelease(obj);
}

void cove_surface_fit(void *container) {
    if (!container) return;
    OnMain(^{
        NSView *view = (__bridge NSView *)container;
        for (NSView *sub in view.subviews)
            sub.frame = view.bounds;
    });
}

void *cove_private_store(void) {
    __block void *result = NULL;
    OnMain(^{
        result = (__bridge_retained void *)[WKWebsiteDataStore nonPersistentDataStore];
    });
    return result;
}

void cove_compile_rules(const char *identifier, const char *json, void *ctx, void (*cb)(void *, void *, const char *)) {
    NSString *ident = [NSString stringWithUTF8String:identifier ? identifier : "cove"];
    NSString *source = [NSString stringWithUTF8String:json ? json : "[]"];
    WKContentRuleListStore *store = WKContentRuleListStore.defaultStore;
    [store compileContentRuleListForIdentifier:ident encodedContentRuleList:source completionHandler:^(WKContentRuleList *list, NSError *error) {
        void *retained = list ? (__bridge_retained void *)list : NULL;
        char *owned = NULL;
        if (error.localizedDescription.UTF8String) owned = strdup(error.localizedDescription.UTF8String);
        dispatch_async(dispatch_get_main_queue(), ^{
            if (cb) cb(ctx, retained, owned);
            free(owned);
        });
    }];
}

static CovePageBox *CreatePage(int isPrivate, void *store, void *rules, const char *script, void *ctx, CovePageCallbacks cbs) {
    CoveDelegate *delegate = [CoveDelegate new];
    CovePageBox *page = [CovePageBox new];
    page.delegate = delegate;
    page.ctx = ctx;
    page.cbs = cbs;
    page.downloads = [NSMutableDictionary dictionary];
    delegate.page = page;

    WKWebViewConfiguration *config = [WKWebViewConfiguration new];
    config.preferences.elementFullscreenEnabled = YES;
    config.preferences.javaScriptCanOpenWindowsAutomatically = YES;
    config.mediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypeNone;
    [config.preferences setValue:@YES forKey:@"developerExtrasEnabled"];
    if (store) config.websiteDataStore = (__bridge WKWebsiteDataStore *)store;
    else if (isPrivate) config.websiteDataStore = [WKWebsiteDataStore nonPersistentDataStore];
    [config setURLSchemeHandler:delegate forURLScheme:@"cove"];
    [config.userContentController addScriptMessageHandler:delegate name:@"cove"];
    if (rules) [config.userContentController addContentRuleList:(__bridge WKContentRuleList *)rules];
    NSString *source = script ? [NSString stringWithUTF8String:script] : @"";
    page.script = source;
    if (source.length) {
        WKUserScript *userScript = [[WKUserScript alloc] initWithSource:source injectionTime:WKUserScriptInjectionTimeAtDocumentStart forMainFrameOnly:NO];
        [config.userContentController addUserScript:userScript];
    }

    WKWebView *webView = [[WKWebView alloc] initWithFrame:NSMakeRect(0, 0, 1100, 800) configuration:config];
    webView.navigationDelegate = delegate;
    webView.UIDelegate = delegate;
    webView.allowsBackForwardNavigationGestures = YES;
    webView.allowsMagnification = YES;
    if (@available(macOS 13.3, *)) webView.inspectable = YES;
    webView.underPageBackgroundColor = [NSColor colorWithSRGBRed:0x12 / 255.0 green:0x13 / 255.0 blue:0x16 / 255.0 alpha:1];
    page.webView = webView;
    [webView addObserver:delegate forKeyPath:@"title" options:NSKeyValueObservingOptionNew context:NULL];
    page.observingTitle = YES;
    [page show:NO];
    return page;
}

void *cove_page_create(int isPrivate, void *store, void *rules, const char *script, void *ctx, const CovePageCallbacks *cbs) {
    __block void *result = NULL;
    CovePageCallbacks copy = cbs ? *cbs : (CovePageCallbacks){0};
    OnMain(^{
        @try {
            CovePageBox *page = CreatePage(isPrivate, store, rules, script, ctx, copy);
            result = (__bridge_retained void *)page;
        } @catch (NSException *ex) {
            fprintf(stderr, "cove page: %s\n", ex.reason.UTF8String ?: "failed");
        }
    });
    return result;
}

void cove_page_destroy(void *ptr) {
    if (!ptr) return;
    OnMain(^{
        CovePageBox *page = (__bridge_transfer CovePageBox *)ptr;
        if (page.observingTitle) {
            @try { [page.webView removeObserver:page.delegate forKeyPath:@"title"]; }
            @catch (__unused NSException *ex) {}
            page.observingTitle = NO;
        }
        page.webView.navigationDelegate = nil;
        page.webView.UIDelegate = nil;
        [page.webView.configuration.userContentController removeScriptMessageHandlerForName:@"cove"];
        [page.webView removeFromSuperview];
        for (WKDownload *download in page.downloads.allValues) {
            [download cancel:^(NSData *data) {}];
        }
        [page.downloads removeAllObjects];
        page.webView = nil;
        page.ctx = NULL;
    });
}

void cove_page_attach(void *ptr, void *container) {
    if (!ptr) return;
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        page.container = (__bridge NSView *)container;
        [page show:page.wantsVisible];
    });
}

void cove_page_show(void *ptr, int visible) {
    if (!ptr) return;
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        [page show:visible ? YES : NO];
    });
}

void cove_page_navigate(void *ptr, const char *url) {
    if (!ptr || !url) return;
    NSString *text = [NSString stringWithUTF8String:url];
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        NSURL *target = [NSURL URLWithString:text];
        if (!target) return;
        [page.webView loadRequest:[NSURLRequest requestWithURL:target]];
    });
}

void cove_page_load_html(void *ptr, const char *html) {
    if (!ptr || !html) return;
    NSString *text = [NSString stringWithUTF8String:html];
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        [page.webView loadHTMLString:text baseURL:nil];
    });
}

void cove_page_back(void *ptr) {
    if (!ptr) return;
    OnMain(^{ [((__bridge CovePageBox *)ptr).webView goBack]; });
}

void cove_page_forward(void *ptr) {
    if (!ptr) return;
    OnMain(^{ [((__bridge CovePageBox *)ptr).webView goForward]; });
}

void cove_page_reload(void *ptr) {
    if (!ptr) return;
    OnMain(^{ [((__bridge CovePageBox *)ptr).webView reload]; });
}

void cove_page_stop(void *ptr) {
    if (!ptr) return;
    OnMain(^{ [((__bridge CovePageBox *)ptr).webView stopLoading]; });
}

void cove_page_focus(void *ptr) {
    if (!ptr) return;
    OnMain(^{
        WKWebView *webView = ((__bridge CovePageBox *)ptr).webView;
        [webView.window makeFirstResponder:webView];
    });
}

int cove_page_can_back(void *ptr) {
    if (!ptr) return 0;
    __block int value = 0;
    OnMain(^{ value = ((__bridge CovePageBox *)ptr).webView.canGoBack ? 1 : 0; });
    return value;
}

int cove_page_can_forward(void *ptr) {
    if (!ptr) return 0;
    __block int value = 0;
    OnMain(^{ value = ((__bridge CovePageBox *)ptr).webView.canGoForward ? 1 : 0; });
    return value;
}

void cove_page_eval(void *ptr, const char *js, void *ctx, void (*cb)(void *, const char *, const char *)) {
    if (!ptr || !js) {
        if (cb) cb(ctx, NULL, "missing page");
        return;
    }
    NSString *source = [NSString stringWithUTF8String:js];
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        [page.webView evaluateJavaScript:source completionHandler:^(id result, NSError *error) {
            if (!cb) return;
            NSString *text = [result isKindOfClass:[NSString class]] ? result : nil;
            if (!text && result && result != NSNull.null) {
                if ([NSJSONSerialization isValidJSONObject:result]) {
                    NSData *data = [NSJSONSerialization dataWithJSONObject:result options:0 error:nil];
                    text = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
                } else if ([result isKindOfClass:[NSNumber class]]) {
                    text = [result stringValue];
                }
            }
            cb(ctx, text.UTF8String, error.localizedDescription.UTF8String);
        }];
    });
}

void cove_page_set_rules(void *ptr, void *rules) {
    if (!ptr) return;
    OnMain(^{
        WKUserContentController *controller = ((__bridge CovePageBox *)ptr).webView.configuration.userContentController;
        [controller removeAllContentRuleLists];
        if (rules) [controller addContentRuleList:(__bridge WKContentRuleList *)rules];
    });
}

void cove_page_set_script(void *ptr, const char *script) {
    if (!ptr) return;
    NSString *source = script ? [NSString stringWithUTF8String:script] : @"";
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        page.script = source;
        InstallScript(page, source);
    });
}

void cove_download_cancel(void *ptr, int ident) {
    if (!ptr) return;
    OnMain(^{
        CovePageBox *page = (__bridge CovePageBox *)ptr;
        WKDownload *download = page.downloads[@(ident)];
        [download cancel:^(NSData *data) {}];
    });
}

int cove_login_check(const char *password) {
    if (!password || !password[0]) return 0;
    __block int ok = 0;
    OnMain(^{
        @autoreleasepool {
            NSError *error = nil;
            ODNode *node = [ODNode nodeWithSession:ODSession.defaultSession type:kODNodeTypeAuthentication error:&error];
            if (!node) return;
            ODRecord *record = [node recordWithRecordType:kODRecordTypeUsers name:NSUserName() attributes:nil error:&error];
            if (!record) return;
            ok = [record verifyPassword:[NSString stringWithUTF8String:password] error:&error] ? 1 : 0;
        }
    });
    return ok;
}

static NSDictionary *KeyQuery(const char *service, const char *account) {
    return @{
        (id)kSecClass: (id)kSecClassGenericPassword,
        (id)kSecAttrService: [NSString stringWithUTF8String:service ? service : ""],
        (id)kSecAttrAccount: [NSString stringWithUTF8String:account ? account : ""]
    };
}

int cove_keychain_save(const char *service, const char *account, const void *bytes, int length) {
    if (!bytes || length <= 0) return 0;
    __block int ok = 0;
    NSData *data = [NSData dataWithBytes:bytes length:(NSUInteger)length];
    OnMain(^{
        NSDictionary *query = KeyQuery(service, account);
        SecItemDelete((__bridge CFDictionaryRef)query);
        NSMutableDictionary *add = [query mutableCopy];
        add[(id)kSecValueData] = data;
        ok = SecItemAdd((__bridge CFDictionaryRef)add, NULL) == errSecSuccess ? 1 : 0;
    });
    return ok;
}

void *cove_keychain_load(const char *service, const char *account, int *outLength) {
    if (outLength) *outLength = 0;
    __block void *result = NULL;
    OnMain(^{
        NSMutableDictionary *query = [KeyQuery(service, account) mutableCopy];
        query[(id)kSecReturnData] = @YES;
        query[(id)kSecMatchLimit] = (id)kSecMatchLimitOne;
        CFTypeRef found = NULL;
        if (SecItemCopyMatching((__bridge CFDictionaryRef)query, &found) != errSecSuccess || !found) return;
        NSData *data = (__bridge_transfer NSData *)found;
        if (data.length == 0) return;
        void *copy = malloc(data.length);
        memcpy(copy, data.bytes, data.length);
        if (outLength) *outLength = (int)data.length;
        result = copy;
    });
    return result;
}

int cove_keychain_exists(const char *service, const char *account) {
    __block int ok = 0;
    OnMain(^{
        ok = SecItemCopyMatching((__bridge CFDictionaryRef)KeyQuery(service, account), NULL) == errSecSuccess ? 1 : 0;
    });
    return ok;
}

void cove_secret_free(void *ptr, int length) {
    if (!ptr) return;
    if (length > 0) memset(ptr, 0, (size_t)length);
    free(ptr);
}

int cove_touch_id_available(void) {
    __block int ok = 0;
    OnMain(^{
        @autoreleasepool {
            LAContext *ctx = [LAContext new];
            NSError *error = nil;
            ok = [ctx canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics error:&error] ? 1 : 0;
        }
    });
    return ok;
}

void cove_touch_id_evaluate(const char *reason, void *ctx, void (*cb)(void *, int)) {
    if (!cb) return;
    NSString *text = reason ? [NSString stringWithUTF8String:reason] : @"Unlock Cove";
    OnMain(^{
        @autoreleasepool {
            LAContext *auth = [LAContext new];
            NSError *error = nil;
            if (![auth canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics error:&error]) {
                cb(ctx, 0);
                return;
            }
            [auth evaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics localizedReason:text reply:^(BOOL success, NSError *evalError) {
                dispatch_async(dispatch_get_main_queue(), ^{ cb(ctx, success ? 1 : 0); });
            }];
        }
    });
}
