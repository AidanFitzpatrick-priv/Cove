using System.IO;
using System.Text.Json;

namespace Cove;

sealed class Store
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _gate = new();

    public static Store Current { get; } = Load();
    public AppState State { get; }
    public string Root { get; }

    Store(AppState state, string root)
    {
        State = state;
        Root = root;
    }

    public static Store Load()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cove");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        AppState state;
        try
        {
            state = File.Exists(path)
                ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Json) ?? new AppState()
                : new AppState();
        }
        catch
        {
            state = new AppState();
        }

        state.AdBlockAllow ??= [];
        state.NeverSave ??= [];
        state.Workspaces ??= [];
        state.Bookmarks ??= [];
        state.History ??= [];
        state.Downloads ??= [];
        state.ExtensionPaths ??= [];
        state.Plugins ??= [];
        foreach (var plugin in state.Plugins)
            plugin.Settings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (state.RamCapMb < 512) state.RamCapMb = 4096;
        return new Store(state, root);
    }

    public void Save()
    {
        lock (_gate)
        {
            var path = Path.Combine(Root, "state.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
