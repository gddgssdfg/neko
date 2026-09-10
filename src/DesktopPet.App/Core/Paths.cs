using System.IO;

namespace DesktopPet.App.Core;

/// <summary>
/// 运行时目录布局（与 desktop-pet-plan.md 5.4 一致）：
///   程序根目录/
///   ├── plugins/           用户可自由增删
///   ├── data/
///   │   ├── config.json
///   │   └── plugins/<id>/  插件私有数据
///   └── assets/            雪碧图
/// </summary>
public static class Paths
{
    /// <summary>程序根目录（exe 所在目录）</summary>
    public static string Root { get; } =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>用户插件目录</summary>
    public static string Plugins => Path.Combine(Root, "plugins");

    /// <summary>运行时数据目录</summary>
    public static string Data => Path.Combine(Root, "data");

    /// <summary>配置文件</summary>
    public static string ConfigFile => Path.Combine(Data, "config.json");

    /// <summary>插件私有数据根</summary>
    public static string PluginData => Path.Combine(Data, "plugins");

    /// <summary>内置素材目录</summary>
    public static string Assets => Path.Combine(Root, "neko-assets");

    /// <summary>某插件的数据目录</summary>
    public static string PluginDataDir(string pluginId) => Path.Combine(PluginData, pluginId);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(PluginData);
    }

    public static void EnsurePluginsCreated()
    {
        Directory.CreateDirectory(Plugins);
    }
}
