using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cove;

static class AdBlock
{
    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "google-analytics.com",
        "googletagmanager.com", "googletagservices.com", "adservice.google.com", "2mdn.net",
        "adnxs.com", "adsrvr.org", "adform.net", "adroll.com", "amazon-adsystem.com",
        "casalemedia.com", "criteo.com", "criteo.net", "demdex.net", "everesttech.net",
        "moatads.com", "outbrain.com", "pubmatic.com", "rubiconproject.com", "scorecardresearch.com",
        "taboola.com", "quantserve.com", "serving-sys.com", "sharethrough.com", "smartadserver.com",
        "bidswitch.net", "openx.net", "media.net", "contextweb.com", "lijit.com", "tapad.com",
        "rlcdn.com", "exelator.com", "bluekai.com", "agkn.com", "mathtag.com", "advertising.com",
        "adsafeprotected.com", "doubleverify.com", "hotjar.com", "hotjar.io", "fullstory.com",
        "mouseflow.com", "mixpanel.com", "segment.io", "segment.com", "newrelic.com", "nr-data.net",
        "clarity.ms", "bat.bing.com", "connect.facebook.net", "pixel.facebook.com", "an.facebook.com",
        "ads-twitter.com", "ads.tiktok.com", "analytics.tiktok.com", "ads.yahoo.com", "imrworldwide.com",
        "chartbeat.com", "chartbeat.net", "omtrdc.net", "2o7.net", "tiqcdn.com", "adobedtm.com",
        "krxd.net", "adsymptotic.com", "ads.linkedin.com", "snapads.com", "sc-static.net",
        "ads.reddit.com", "moatpixel.com", "creativecdn.com", "adition.com", "adscale.de",
        "pubads.g.doubleclick.net", "securepubads.g.doubleclick.net", "googleads.g.doubleclick.net"
    };

    public static bool ShouldBlock(Uri request, string pageHost, bool enabled, IReadOnlyCollection<string> allow)
    {
        if (!enabled) return false;
        if (string.IsNullOrEmpty(pageHost)) return false;
        if (allow.Contains(pageHost, StringComparer.OrdinalIgnoreCase)) return false;
        if (request.Host.Equals("cove.local", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsYouTubeAd(request)) return true;
        if (SameSite(request.Host, pageHost)) return false;
        return IsBlockedHost(request.Host);
    }

    static bool IsYouTubeAd(Uri uri)
    {
        var host = uri.Host;
        var youtube = host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
        if (!youtube) return false;
        var path = uri.AbsolutePath;
        return path.Contains("pagead", StringComparison.OrdinalIgnoreCase)
            || path.Contains("ptracking", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/api/stats/ads", StringComparison.OrdinalIgnoreCase)
            || path.Contains("get_midroll", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/ad_break", StringComparison.OrdinalIgnoreCase);
    }

    static bool SameSite(string requestHost, string pageHost)
    {
        requestHost = requestHost.Trim('.').ToLowerInvariant();
        pageHost = pageHost.Trim('.').ToLowerInvariant();
        return requestHost == pageHost
            || requestHost.EndsWith("." + pageHost, StringComparison.Ordinal)
            || pageHost.EndsWith("." + requestHost, StringComparison.Ordinal);
    }

    public static string BuildRules(IReadOnlyCollection<string> allow)
    {
        var unless = allow
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim().TrimStart('.').ToLowerInvariant())
            .Where(host => host.Length > 0 && !host.Contains(' ') && !host.Contains('*'))
            .Distinct(StringComparer.Ordinal)
            .Select(host => "*" + host)
            .Take(80)
            .ToArray();
        var rules = new JsonArray();
        foreach (var pattern in HostFilters(Hosts))
            rules.Add(Rule(pattern, thirdParty: true, unless));
        foreach (var path in new[] { "pagead", "ptracking", "api/stats/ads", "get_midroll", "ad_break" })
            rules.Add(Rule("https?://([^/]*\\.)?youtube\\.com/.+" + Regex.Escape(path), thirdParty: false, unless));
        return rules.ToJsonString();
    }

    static IEnumerable<string> HostFilters(IEnumerable<string> hosts)
    {
        foreach (var host in hosts)
            yield return "https?://([^/]*\\.)?" + Regex.Escape(host);
    }

    static JsonObject Rule(string filter, bool thirdParty, string[] unless)
    {
        var trigger = new JsonObject { ["url-filter"] = filter };
        if (thirdParty) trigger["load-type"] = new JsonArray("third-party");
        if (unless.Length > 0)
        {
            var domains = new JsonArray();
            foreach (var host in unless) domains.Add(host);
            trigger["unless-domain"] = domains;
        }
        return new JsonObject
        {
            ["trigger"] = trigger,
            ["action"] = new JsonObject { ["type"] = "block" }
        };
    }

    static bool IsBlockedHost(string host)
    {
        host = host.Trim('.').ToLowerInvariant();
        if (Hosts.Contains(host)) return true;
        while (true)
        {
            var dot = host.IndexOf('.');
            if (dot < 0) return false;
            host = host[(dot + 1)..];
            if (!host.Contains('.')) return false;
            if (Hosts.Contains(host)) return true;
        }
    }
}
