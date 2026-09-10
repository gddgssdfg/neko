using DesktopPet.Core;

namespace DesktopPet.Runtime;

/// <summary>manifest 权限声明 → PluginPermissions 位掩码</summary>
public static class PermissionChecker
{
    private static readonly Dictionary<string, PluginPermissions> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pet.animate"]  = PluginPermissions.PetAnimate,
        ["pet.move"]     = PluginPermissions.PetMove,
        ["pet.read"]     = PluginPermissions.PetRead,
        ["ui.register"]  = PluginPermissions.UIRegister,
        ["storage.local"]= PluginPermissions.Storage,
        ["scheduler"]    = PluginPermissions.Scheduler,
        ["notify"]       = PluginPermissions.Notify,
        ["clipboard"]    = PluginPermissions.Clipboard,
        ["network"]      = PluginPermissions.Network,
        ["file"]         = PluginPermissions.FileAccess,
        ["process"]      = PluginPermissions.Process,
    };

    /// <summary>合并 manifest 中所有权限段声明为位掩码</summary>
    public static PluginPermissions FromManifest(PluginManifest m)
    {
        var result = PluginPermissions.None;
        // pet 段的项自动加 "pet." 前缀，ui 段加 "ui."，storage 段本身已是完整键（如 "local" → storage.local）
        result |= ToFlags(m.Permissions.Pet, "pet.");
        result |= ToFlags(m.Permissions.Ui, "ui.");
        result |= ToFlags(m.Permissions.Storage, "storage.");
        result |= ToFlags(m.Permissions.Other, "");
        return result;
    }

    private static PluginPermissions ToFlags(IEnumerable<string> tokens, string prefix)
    {
        var result = PluginPermissions.None;
        foreach (var token in tokens)
        {
            var key = prefix + token.Trim();
            if (Map.TryGetValue(key, out var flag))
                result |= flag;
        }
        return result;
    }

    private static bool Allowed(PluginPermissions granted, PluginPermissions requested)
        => (granted & requested) == requested;

    /// <summary>是否包含高风险权限（安装时需红色警告）</summary>
    public static bool IsHighRisk(PluginPermissions p)
        => (p & (PluginPermissions.FileAccess | PluginPermissions.Process)) != 0;
}
