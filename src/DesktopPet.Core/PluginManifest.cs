namespace DesktopPet.Core;

/// <summary>插件清单（plugin.toml 的强类型模型）</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.1.0";
    public int Api { get; set; } = 1;
    public string Entry { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Homepage { get; set; }

    public ManifestPermissions Permissions { get; set; } = new();
    public ManifestUi Ui { get; set; } = new();
    public ManifestResources Resources { get; set; } = new();
}

public sealed class ManifestPermissions
{
    public string[] Pet { get; set; } = [];
    public string[] Ui { get; set; } = [];
    public string[] Storage { get; set; } = [];
    public string[] Other { get; set; } = [];
}

public sealed class ManifestUi
{
    public bool Radial { get; set; }
    public bool Settings { get; set; }
}

public sealed class ManifestResources
{
    public int MaxMemoryMB { get; set; } = 16;
}
