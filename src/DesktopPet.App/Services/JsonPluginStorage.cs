using System.IO;
using DesktopPet.Core;

namespace DesktopPet.App.Services;

/// <summary>轻量 KV 存储（每插件隔离目录）</summary>
public sealed class JsonPluginStorage : IPluginStorage
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly System.Text.Json.JsonSerializerOptions _opts = new() { WriteIndented = true };

    public JsonPluginStorage(string dataDir)
    {
        _dir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(_dir);
    }

    public T? Get<T>(string key)
    {
        var path = GetPath(key);
        lock (_gate)
        {
            if (!File.Exists(path)) return default;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), _opts);
            }
            catch { return default; }
        }
    }

    public void Set<T>(string key, T value)
    {
        var path = GetPath(key);
        // Serialize before touching disk: a serialization failure must leave the old value intact.
        var json = System.Text.Json.JsonSerializer.Serialize(value, _opts);
        lock (_gate)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public void Remove(string key)
    {
        var path = GetPath(key);
        lock (_gate) File.Delete(path);
    }

    public bool Contains(string key) => File.Exists(GetPath(key));

    private string GetPath(string key)
    {
        // Keys are filenames, not paths. Reject traversal, drive paths and NTFS alternate streams.
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || key is "." or "..")
            throw new ArgumentException("Storage key must be a non-empty filename.", nameof(key));
        return Path.Combine(_dir, key + ".json");
    }
}
