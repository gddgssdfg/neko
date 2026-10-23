using DesktopPet.Core;
using Tomlyn;
using Tomlyn.Model;

namespace DesktopPet.Runtime;

/// <summary>plugin.toml 解析</summary>
public static class ManifestParser
{
    public static PluginManifest Parse(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var doc = Toml.Parse(text);
        if (doc.HasErrors)
        {
            throw new FormatException($"plugin.toml 解析失败 ({filePath}):\n{string.Join("\n", doc.Diagnostics)}");
        }

        var model = doc.ToModel();
        if (!model.TryGetValue("plugin", out var pluginRaw) || pluginRaw is not TomlTable plugin)
        {
            throw new FormatException($"缺少 [plugin] 段: {filePath}");
        }

        var m = new PluginManifest
        {
            Id = Str(plugin, "id") ?? "",
            Name = Str(plugin, "name") ?? "",
            Version = Str(plugin, "version") ?? "0.1.0",
            Api = Int(plugin, "api") ?? 1,
            Entry = Str(plugin, "entry") ?? "",
            Author = Str(plugin, "author") ?? "",
            Description = Str(plugin, "description") ?? "",
            Homepage = Str(plugin, "homepage"),
        };

        if (string.IsNullOrEmpty(m.Id))
            throw new FormatException($"plugin.toml 缺少必填字段 [plugin].id: {filePath}");
        if (string.IsNullOrEmpty(m.Entry))
            throw new FormatException($"plugin.toml 缺少必填字段 [plugin].entry: {filePath}");

        if (model.TryGetValue("permissions", out var pRaw) && pRaw is TomlTable p)
        {
            m.Permissions.Pet = Arr(p, "pet");
            m.Permissions.Ui = Arr(p, "ui");
            m.Permissions.Storage = Arr(p, "storage");
            m.Permissions.Other = Arr(p, "other");
        }

        if (model.TryGetValue("ui", out var uiRaw) && uiRaw is TomlTable ui)
        {
            m.Ui.Radial = Bool(ui, "radial") ?? false;
            m.Ui.Settings = Bool(ui, "settings") ?? false;
        }

        if (model.TryGetValue("resources", out var rRaw) && rRaw is TomlTable res)
        {
            m.Resources.MaxMemoryMB = Int(res, "maxMemoryMB") ?? 16;
        }

        return m;
    }

    private static string? Str(TomlTable t, string k) =>
        t.TryGetValue(k, out var v) ? v?.ToString() : null;

    private static int? Int(TomlTable t, string k) =>
        !t.TryGetValue(k, out var v) ? null : v is long l && l >= int.MinValue && l <= int.MaxValue
            ? (int)l : throw new FormatException($"plugin.toml 字段 '{k}' 必须为 32 位整数");

    private static bool? Bool(TomlTable t, string k) =>
        t.TryGetValue(k, out var v) && v is bool b ? b : null;

    private static string[] Arr(TomlTable t, string k)
    {
        if (!t.TryGetValue(k, out var v)) return [];
        if (v is not TomlArray a || a.Any(item => item is not string))
            throw new FormatException($"plugin.toml 权限字段 '{k}' 必须为字符串数组");
        return a.Cast<string>().ToArray();
    }
}
