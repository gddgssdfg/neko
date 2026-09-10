using System.IO;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>轻量 KV 存储（每插件隔离目录）</summary>
public sealed class JsonPluginStorage : IPluginStorage
{
    private readonly string _dir;
    private readonly System.Text.Json.JsonSerializerOptions _opts = new() { WriteIndented = true };

    public JsonPluginStorage(string dataDir)
    {
        _dir = dataDir;
        Directory.CreateDirectory(_dir);
    }

    public T? Get<T>(string key)
    {
        var path = Path.Combine(_dir, key + ".json");
        if (!File.Exists(path)) return default;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), _opts);
        }
        catch { return default; }
    }

    public void Set<T>(string key, T value)
    {
        var path = Path.Combine(_dir, key + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, _opts));
    }

    public void Remove(string key)
    {
        var path = Path.Combine(_dir, key + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Contains(string key) => File.Exists(Path.Combine(_dir, key + ".json"));
}
