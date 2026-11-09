using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cove;

sealed class PluginManager
{
    static readonly Regex SafeId = new("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    readonly AppState _state;
    readonly string _root;
    long _generation = 1;

    public PluginManager(AppState state, string appRoot)
    {
        _state = state;
        _root = Path.Combine(appRoot, "plugins");
        Directory.CreateDirectory(_root);
        SharedRoot = _root;
    }

    public static string? SharedRoot { get; private set; }

    public string Root => _root;
    public IReadOnlyList<PluginVm> Items { get; private set; } = [];

    public void Refresh()
    {
        var list = new List<PluginVm>();
        foreach (var dir in Directory.Exists(_root) ? Directory.GetDirectories(_root) : [])
        {
            var manifestPath = Path.Combine(dir, "cove-plugin.json");
            if (!File.Exists(manifestPath)) continue;
            PluginManifest? manifest = null;
            string? error = null;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Json);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)) error = "Manifest is missing an id.";
                else if (!IsSafeId(manifest.Id)) error = "Plugin id must be letters, numbers, and dashes.";
                else if (!string.Equals(Path.GetFileName(dir), manifest.Id, StringComparison.OrdinalIgnoreCase)) error = "Plugin folder name must match its id.";
                else if (string.IsNullOrWhiteSpace(manifest.Entry) || !IsInside(dir, Path.Combine(dir, manifest.Entry.Replace('/', Path.DirectorySeparatorChar)))) error = "Plugin entry file is missing.";
                else if (!File.Exists(Path.Combine(dir, manifest.Entry.Replace('/', Path.DirectorySeparatorChar)))) error = "Plugin entry file is missing.";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            var id = manifest?.Id ?? Path.GetFileName(dir);
            var record = Find(id);
            list.Add(new PluginVm
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(manifest?.Name) ? id : manifest!.Name,
                Version = manifest?.Version ?? "",
                Description = manifest?.Description ?? "",
                Folder = dir,
                Host = HostFor(id),
                Entry = manifest?.Entry ?? "",
                Enabled = record?.Enabled ?? true,
                Error = error,
                HasSettingsPage = File.Exists(Path.Combine(dir, "settings.html")),
                Fields = manifest?.Settings ?? []
            });
        }
        Items = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? Install(string path)
    {
        try
        {
            if (Directory.Exists(path)) return InstallFolder(path);
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return InstallZip(path);
            if (File.Exists(path) && Path.GetFileName(path).Equals("cove-plugin.json", StringComparison.OrdinalIgnoreCase))
                return InstallFolder(Path.GetDirectoryName(path)!);
            return "Drop a plugin folder or a zip.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void Remove(string id)
    {
        var dir = Path.Combine(_root, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        _state.Plugins.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Refresh();
    }

    public void SetEnabled(string id, bool enabled)
    {
        var record = Ensure(id);
        record.Enabled = enabled;
        Refresh();
    }

    public void SetSetting(string id, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("password", StringComparison.OrdinalIgnoreCase)) return;
        var record = Ensure(id);
        record.Settings[key] = value;
        Refresh();
    }

    public string? CopySettingFile(string id, string key, string source)
    {
        var plugin = Items.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (plugin?.Folder is null || !File.Exists(source)) return "That file is missing.";
        var data = Path.Combine(plugin.Folder, "data");
        Directory.CreateDirectory(data);
        var name = Regex.Replace(Path.GetFileName(source), @"[^a-zA-Z0-9._-]", "");
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        var dest = Path.Combine(data, name);
        File.Copy(source, dest, overwrite: true);
        SetSetting(id, key, "data/" + name);
        return null;
    }

    public static IEnumerable<(string Host, string Folder)> MappedFolders()
    {
        var root = SharedRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, "cove-plugin.json"))) continue;
            yield return ("plugin-" + Path.GetFileName(dir).ToLowerInvariant() + ".cove.local", dir);
        }
    }

    public static bool TryRead(Uri uri, out byte[] bytes, out string mime)
    {
        bytes = [];
        mime = "application/octet-stream";
        if (!TryPath(uri, out var path)) return false;
        try
        {
            bytes = File.ReadAllBytes(path);
            mime = Mime(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryPath(Uri uri, out string path)
    {
        path = "";
        if (uri.Scheme is not ("https" or "http" or "cove")) return false;
        const string prefix = "plugin-";
        const string suffix = ".cove.local";
        var host = uri.Host;
        if (host.Length <= prefix.Length + suffix.Length
            || !host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var id = host[prefix.Length..^suffix.Length];
        if (!SafeId.IsMatch(id)) return false;
        var root = SharedRoot;
        if (string.IsNullOrWhiteSpace(root)) return false;
        var pluginRoot = Path.GetFullPath(Path.Combine(root, id));
        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal)) return false;
        var full = Path.GetFullPath(Path.Combine(pluginRoot, relative));
        var rootPrefix = pluginRoot.EndsWith(Path.DirectorySeparatorChar) ? pluginRoot : pluginRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
        path = full;
        return true;
    }

    static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".html" => "text/html",
        _ => "application/octet-stream"
    };

    public string InjectionScript()
    {
        var payload = new List<object>();
        foreach (var plugin in Items)
        {
            if (!plugin.Enabled || !string.IsNullOrWhiteSpace(plugin.Error) || string.IsNullOrWhiteSpace(plugin.Folder)) continue;
            var entryPath = Path.Combine(plugin.Folder, plugin.Entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryPath)) continue;
            payload.Add(new
            {
                id = plugin.Id,
                name = plugin.Name,
                host = plugin.Host,
                origin = (OperatingSystem.IsMacOS() ? "cove://" : "https://") + plugin.Host,
                gen = plugin.Id + ":" + _generation + ":" + File.GetLastWriteTimeUtc(entryPath).Ticks,
                code = File.ReadAllText(entryPath),
                settings = ResolvedSettings(plugin)
            });
        }
        return WebScripts.PluginBoot.Replace("__PLUGINS__", JsonSerializer.Serialize(payload), StringComparison.Ordinal);
    }

    public void Bump() => _generation++;

    public Dictionary<string, string> ResolvedSettings(PluginVm plugin)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var record = Find(plugin.Id);
        foreach (var field in plugin.Fields)
        {
            var raw = record?.Settings.TryGetValue(field.Id, out var saved) == true && !string.IsNullOrWhiteSpace(saved)
                ? saved
                : field.Default ?? "";
            if (field.Type.Equals("file", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(raw) && !raw.Contains("://", StringComparison.Ordinal))
                raw = (OperatingSystem.IsMacOS() ? "cove://" : "https://") + plugin.Host + "/" + raw.TrimStart('/');
            values[field.Id] = raw;
        }
        if (record is not null)
        {
            foreach (var pair in record.Settings)
            {
                if (!values.ContainsKey(pair.Key)) values[pair.Key] = pair.Value;
            }
        }
        return values;
    }

    string? InstallZip(string zipPath)
    {
        var temp = Path.Combine(Path.GetTempPath(), "cove-plugin-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temp);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.GetFullPath(Path.Combine(temp, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                var root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "That zip tried to write outside its folder.";
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            return InstallFolder(PackageRoot(temp));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* The folder is only a staging copy. */ }
        }
    }

    string? InstallFolder(string source)
    {
        source = PackageRoot(source);
        var manifestPath = Path.Combine(source, "cove-plugin.json");
        if (!File.Exists(manifestPath)) return "That folder has no cove-plugin.json.";
        PluginManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Json); }
        catch (Exception ex) { return ex.Message; }
        if (manifest is null || !IsSafeId(manifest.Id)) return "Plugin id must be letters, numbers, and dashes.";
        var dest = Path.Combine(_root, manifest.Id);
        var kept = Path.Combine(Path.GetTempPath(), "cove-plugin-data-" + Guid.NewGuid().ToString("n"));
        var data = Path.Combine(dest, "data");
        if (Directory.Exists(data)) Directory.Move(data, kept);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        CopyFolder(source, dest);
        if (Directory.Exists(kept)) Directory.Move(kept, Path.Combine(dest, "data"));
        var existing = Find(manifest.Id);
        if (existing is null)
            _state.Plugins.Add(new PluginState { Id = manifest.Id, Enabled = true });
        Bump();
        Refresh();
        return null;
    }

    static string PackageRoot(string path)
    {
        if (File.Exists(Path.Combine(path, "cove-plugin.json"))) return path;
        var dirs = Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "cove-plugin.json"))) return dirs[0];
        return path;
    }

    static void CopyFolder(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyFolder(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    static bool IsSafeId(string? id) => !string.IsNullOrWhiteSpace(id) && SafeId.IsMatch(id);

    static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    static string HostFor(string id) => "plugin-" + id.ToLowerInvariant() + ".cove.local";

    PluginState? Find(string id) => _state.Plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    PluginState Ensure(string id)
    {
        var found = Find(id);
        if (found is not null) return found;
        found = new PluginState { Id = id, Enabled = true };
        _state.Plugins.Add(found);
        return found;
    }

    public sealed class PluginManifest
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Entry { get; set; } = "main.js";
        public List<PluginField> Settings { get; set; } = [];
    }

    public sealed class PluginField
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "string";
        public string? Default { get; set; }
        public List<string> Options { get; set; } = [];
    }
}

sealed class PluginVm
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Folder { get; init; }
    public string Host { get; init; } = "";
    public string Entry { get; init; } = "";
    public bool Enabled { get; init; }
    public string? Error { get; init; }
    public bool HasSettingsPage { get; init; }
    public List<PluginManager.PluginField> Fields { get; init; } = [];
}
