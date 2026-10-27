using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Core;
using DesktopPet.App.Renderer;

namespace DesktopPet.App.Services;

/// <summary>复用导入契约校验，补充可供作者处理的美术提示。警告不会改写素材。</summary>
public sealed record SkinReview(SkinInfo Info, SpriteAtlas Atlas, IReadOnlyList<string> Warnings)
{
    public static SkinReview Open(string directory)
    {
        var atlas = ExtensionInstaller.InspectSkin(directory);
        var info = SkinRegistry.InspectFolder(directory);
        var warnings = new List<string>();
        if (!File.Exists(Path.Combine(directory, "thumb.png"))) warnings.Add("缺少 thumb.png，外观列表可能没有缩略图。");
        foreach (var key in info.EyeLayers)
        {
            var eye = atlas.GetEye(key);
            if (eye is null) warnings.Add($"眼层 {key} 缺失或无法解码，运行时会跳过。");
            else if (eye.PixelWidth != info.FrameWidth || eye.PixelHeight != info.FrameHeight)
                warnings.Add($"眼层 {key} 尺寸应与单帧 {info.FrameWidth}×{info.FrameHeight} 一致。");
        }
        long budget = 32_000_000;
        foreach (var state in atlas.States.Values)
        {
            if (state.EyeLayer is { } key && atlas.GetEye(key) is null)
                warnings.Add($"动作 {state.Name} 引用了不可用的眼层 {key}。");
            var pixels = (long)info.FrameWidth * info.FrameHeight * state.Frames.Length;
            if (pixels > budget) { warnings.Add($"动作 {state.Name} 较大，跳过像素重复检查；请人工逐帧检查。"); continue; }
            budget -= pixels;
            if (state.Fps <= 0) continue;
            var hashes = new HashSet<string>();
            foreach (var frame in state.Frames)
            {
                var bitmap = new FormatConvertedBitmap(frame.Bitmap, PixelFormats.Pbgra32, null, 0);
                var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
                hashes.Add(Convert.ToHexString(SHA256.HashData(data)));
            }
            if (hashes.Count == 1) warnings.Add($"动作 {state.Name} 的 fps>0，但所有身体帧相同；若为静态姿态请使用 fps=0。");
        }
        return new(info, atlas, warnings);
    }
}
