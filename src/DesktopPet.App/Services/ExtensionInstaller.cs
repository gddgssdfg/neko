using System.IO;
using System.Text.RegularExpressions;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;
using DesktopPet.Core;
using DesktopPet.Runtime;

namespace DesktopPet.App.Services;

public static class ExtensionInstaller
{
    private static readonly HashSet<string> PluginFiles = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".json", ".toml", ".png", ".txt", ".md", ".config" };
    private static readonly HashSet<string> SkinFiles = new(StringComparer.OrdinalIgnoreCase) { ".json", ".png", ".txt", ".md" };

    public static string PermissionSummary(PluginManifest manifest)
    {
        var p = manifest.Permissions;
        var all = p.Pet.Select(x => "pet." + x).Concat(p.Ui.Select(x => "ui." + x))
            .Concat(p.Storage.Select(x => "storage." + x)).Concat(p.Other).ToArray();
        return all.Length == 0 ? "未声明宿主能力" : string.Join("、", all.Select(x => x.ToLowerInvariant() switch
        {
            "pet.animate" => "猫咪动作", "pet.move" => "移动猫咪", "pet.read" => "读取猫咪状态",
            "ui.register" => "显示菜单与设置", "storage.local" => "保存本地数据", "scheduler" => "定时任务",
            "notify" => "桌面通知", "clipboard" => "读取和写入剪贴板", "network" => "网络访问",
            "file" => "访问本机文件", "process" => "启动其他程序", "file.own" => "访问插件目录", "media" => "媒体播放控制", _ => x
        }));
    }

    public static PluginManifest InspectPlugin(string source)
    {
        CheckFiles(source, PluginFiles);
        var manifest = ManifestParser.Parse(Path.Combine(source, "plugin.toml"));
        CheckId(manifest.Id);
        if (manifest.Api != 1) throw new FormatException($"不支持 API {manifest.Api}，当前支持 API 1。");
        PermissionChecker.FromManifest(manifest);
        if (Path.IsPathRooted(manifest.Entry) || manifest.Entry.Contains(':')) throw new FormatException("入口必须在插件目录内。");
        var root = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
        var entry = Path.GetFullPath(Path.Combine(source, manifest.Entry));
        if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry) ||
            !entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new FormatException("插件入口 DLL 缺失或超出目录。");
        return manifest; // Deliberately never loads an assembly.
    }

    public static System.Windows.Media.ImageSource SkinPreview(SpriteAtlas atlas)
    {
        var state = atlas.States["idle"]; var frame = state.Frames[0].Bitmap;
        var group = new System.Windows.Media.DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.DrawImage(frame, new System.Windows.Rect(0,0,frame.PixelWidth,frame.PixelHeight));
            if (state.EyeLayer is { } key && atlas.GetEye(key) is { } eye)
            {
                var offset = state.EyeOffsets.FirstOrDefault();
                drawing.DrawImage(eye, new System.Windows.Rect(offset.X,offset.Y,eye.PixelWidth,eye.PixelHeight));
            }
        }
        group.Freeze(); var preview = new System.Windows.Media.DrawingImage(group); preview.Freeze(); return preview;
    }

    public static SpriteAtlas InspectSkin(string source)
    { CheckFiles(source, SkinFiles); return SpriteAtlas.InspectFolder(source); }

    /// <summary>从重新校验的私有副本打包；目标必须是新文件，不覆盖已有包或源素材。</summary>
    public static void ExportSkin(string source, string destination, string expectedId)
    {
        var input = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var output = Path.GetFullPath(destination);
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 .zip 文件。");
        if (output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请将皮肤包保存到源素材目录之外。");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("目标已存在，请使用新文件名；不会覆盖原文件。");
        var parent = Path.GetDirectoryName(output)!;
        var stage = Path.Combine(parent, ".neko-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var id = Install(source, stage, false, expectedId);
            var zip = Path.Combine(stage, "package.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(stage, id), zip,
                System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
            File.Move(zip, output, overwrite: false);
        }
        finally
        {
            if (string.Equals(Path.GetDirectoryName(stage), parent, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    public static string Install(string source, string destinationRoot, bool plugin, string? expectedId = null)
    {
        var id = plugin ? InspectPlugin(source).Id : InspectSkin(source).Skin;
        CheckId(id);
        if (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal)) throw new IOException("预览后扩展 id 已改变，请重新预览。");
        Directory.CreateDirectory(destinationRoot);
        var root = Path.GetFullPath(destinationRoot);
        var destination = Path.Combine(root, id);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("同名扩展已存在，未覆盖原内容。请使用不同 id，或先备份并移走旧版本。");
        var stage = Path.Combine(root, ".neko-import-" + Guid.NewGuid().ToString("N"));
        var stagedPackage = Path.Combine(stage, id);
        try
        {
            Directory.CreateDirectory(stagedPackage);
            var files = CheckFiles(source, plugin ? PluginFiles : SkinFiles);
            foreach (var file in files)
            {
                var target = Path.Combine(stagedPackage, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, false);
            }
            if (plugin && !string.Equals(InspectPlugin(stagedPackage).Id, id, StringComparison.Ordinal)) throw new IOException("复制期间插件 id 发生变化，已取消安装。");
            if (!plugin) InspectSkin(stagedPackage);
            Directory.Move(stagedPackage, destination);
            return id;
        }
        finally
        {
            // Only the fresh private staging directory is removed; never the existing package.
            if (stage.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(stage))
                Directory.Delete(stage, true);
        }
    }

    private static void CheckId(string id)
    { if (!Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$") || id is "." or "..") throw new FormatException("id 只能包含字母、数字、点、横线和下划线。"); }

    private static List<string> CheckFiles(string source, HashSet<string> allowed)
    {
        var root = new DirectoryInfo(source);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("请选择真实扩展目录，不支持链接目录。");
        var files = new List<string>(); long bytes = 0; int directories = 0;
        void Walk(DirectoryInfo directory)
        {
            if (++directories > 128) throw new IOException("扩展包目录层级过多，请整理目录后重试。");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("扩展包不能包含符号链接或目录联接。");
                if (entry is DirectoryInfo child) Walk(child);
                else if (entry is FileInfo file)
                {
                    if (!allowed.Contains(file.Extension)) throw new IOException($"不支持的包文件：{file.Name}。请选择已整理的扩展目录。");
                    if (file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        using var stream = file.OpenRead();
                        Span<byte> header = new byte[24];
                        if (stream.Read(header) != 24 || !header[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }))
                            throw new IOException($"不是有效 PNG：{file.Name}");
                        int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
                        int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
                        if (w <= 0 || h <= 0 || (long)w * h > 32_000_000) throw new IOException($"图像尺寸过大：{file.Name}");
                    }
                    bytes += file.Length; files.Add(file.FullName);
                    if (files.Count > 512 || bytes > 256L * 1024 * 1024 || file.Length > 64L * 1024 * 1024)
                        throw new IOException("扩展包过大（最多 512 个文件、256 MB，单文件 64 MB）。");
                }
            }
        }
        Walk(root); return files;
    }
}
