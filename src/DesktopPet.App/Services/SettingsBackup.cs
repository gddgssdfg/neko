using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.App.Core;

namespace DesktopPet.App.Services;

/// <summary>Versioned preferences only. Never exports plugin storage, credentials or unknown extension fields.</summary>
public static class SettingsBackup
{
    private const int MaxBytes = 128 * 1024;
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    { "Extra", "SafeMode", "StartWithWindows", "DisabledPlugins", "LastUpdateCheckDay", "PosX", "PosY", "MirrorPosX", "MirrorPosY" };
    private static readonly HashSet<string> Allowed = typeof(AppConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.CanWrite && !Excluded.Contains(p.Name)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    public static string PendingPath => Path.Combine(Paths.Data, "settings-pending.json");

    public static string Export(AppConfig config)
    {
        var settings = JsonSerializer.SerializeToNode(config)!.AsObject();
        foreach (var key in settings.Select(x => x.Key).ToArray()) if (!Allowed.Contains(key)) settings.Remove(key);
        return new JsonObject { ["format"] = "neko-settings", ["version"] = 1, ["settings"] = settings }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static AppConfig Preview(string json, AppConfig current)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("设置文件超过 128 KB。");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.GetString() != "neko-settings" ||
            !root.TryGetProperty("version", out var version) || version.GetInt32() != 1 || !root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("不支持的设置备份格式或版本。");
        var target = JsonSerializer.SerializeToNode(current)!.AsObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in settings.EnumerateObject())
        {
            if (!Allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidDataException("包含不支持或重复的设置项：" + property.Name);
            target[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        // Reuse normal configuration validation without touching the source file.
        return ConfigManager.Parse(target.ToJsonString());
    }

    public static string Read(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("设置文件超过 128 KB。");
        return File.ReadAllText(path);
    }

    public static void Stage(string json, AppConfig current, string path)
    {
        Preview(json, current);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? ApplyPending(string pendingPath, string configPath)
    {
        if (!File.Exists(pendingPath)) return null;
        var current = File.Exists(configPath) ? ConfigManager.Parse(File.ReadAllText(configPath)) : new AppConfig();
        var imported = Preview(Read(pendingPath), current);
        var backup = configPath + ".before-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        if (File.Exists(configPath)) File.Copy(configPath, backup, false);
        ConfigManager.Save(configPath, imported);
        var written = ConfigManager.Parse(File.ReadAllText(configPath));
        if (JsonSerializer.Serialize(written) != JsonSerializer.Serialize(imported)) throw new IOException("设置未能完整保存；原配置备份已保留。");
        File.Delete(pendingPath);
        return backup;
    }
}
