using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Screenshot;

/// <summary>
/// 截图插件：全屏截图保存到插件数据目录（data/plugins/com.lantern.screenshot/）。
/// 展示插件直接 P/Invoke + System.Drawing 的能力。
/// <para>v0.1.84：加入<b>保留策略</b> —— 每次截图成功后清理超额的旧图，只保留最近
/// <see cref="MaxRetainedShots"/> 张，避免数据目录随使用无限增长。</para>
/// </summary>
public sealed class ScreenshotPlugin : PluginBase
{
    /// <summary>
    /// 保留的最近截图张数上限。取 <b>30</b> 的理由：4K 全屏 PNG 单张约 3~8MB，
    /// 30 张约 0.1~0.25GB —— 既足够「最近随手找回 / 回看几天」，又不会无限增长拖垮磁盘；
    /// 需要长期留存的用户可以自行把图片移出该目录（本插件只清理自己命名模式的文件，不动用户文件）。
    /// </summary>
    private const int MaxRetainedShots = 30;

    /// <summary>本插件写盘的文件名前缀 / 后缀，用于识别「确实是本插件产生的文件」，避免误删用户手动放入的文件。</summary>
    private const string ShotFilePrefix = "shot_";
    private const string ShotFileSuffix = ".png";

    private string _dataDir = "";

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public override string Id => "com.lantern.screenshot";
    public override string Name => "截图";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister | PluginPermissions.FileOwnDir;   // v0.1.80：由粗 token FileAccess 细化为 FileOwnDir（仅写自己数据目录，低危）

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);
        _dataDir = ctx.PluginDataDir;
        Directory.CreateDirectory(_dataDir);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "shot-take",
            Label = "截图",
            Icon = "camera",
            Order = 26,
            OnInvoke = TakeScreenshot,
        });
    }

    /// <summary>
    /// 截图并保存到插件数据目录。
    /// <para>v0.1.82：把「采集 + 编码 + 写盘」整体挪到线程池（<see cref="Task.Run(Action)"/>），
    /// 避免 4K 全屏截图在 UI 线程同步执行 <c>CopyFromScreen</c> + PNG 编码而卡住猫动画。</para>
    /// <para>完成后用 <c>Pet.Say</c> 提示；<c>IPetApi</c> 在宿主侧已统一封送到 UI 线程
    /// （见 src/DesktopPet.App/Services/PetApiImpl.cs），故可放心从线程池调用。</para>
    /// </summary>
    private void TakeScreenshot()
    {
        // 采集/编码/写盘都在后台线程；异常绝不静默吞（失败也要给用户可读提示）。
        _ = Task.Run(() =>
        {
            try
            {
                var w = GetSystemMetrics(SM_CXSCREEN);
                var h = GetSystemMetrics(SM_CYSCREEN);
                using var bmp = new Bitmap(w, h);
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(0, 0, 0, 0, bmp.Size);

                // 保持原有文件命名 / 保存目录逻辑不变
                var path = Path.Combine(_dataDir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                bmp.Save(path, ImageFormat.Png);

                // v0.1.84：截图成功后再清理旧图；清理失败不影响「本次截图已成功」这一事实。
                PruneOldShots();

                Pet.Say($"截图已保存：{path}", 4000);   // 宿主侧已封送到 UI 线程，安全
            }
            catch (Exception ex)
            {
                Pet.Say($"截图失败：{ex.Message}", 3000);
            }
        });
    }

    /// <summary>
    /// 保留策略：按修改时间倒序排列「本插件自己写的截图」，删除第 <see cref="MaxRetainedShots"/> 张之后的部分。
    /// <para>要点：
    /// ① 只在截图<b>成功后</b>调用，且自身异常全部吞掉 —— 清理失败绝不影响本次截图结果；
    /// ② 只清理文件名严格匹配本插件模式（<c>shot_yyyyMMdd_HHmmss.png</c>）的文件，
    ///    用户手动放进该目录的其它文件一律不动；
    /// ③ 按 <see cref="FileInfo.LastWriteTimeUtc"/> 判断新旧（比文件名更稳，可容忍改名 / 时钟小偏差）。</para>
    /// </summary>
    private void PruneOldShots()
    {
        try
        {
            if (!Directory.Exists(_dataDir)) return;

            var stale = new DirectoryInfo(_dataDir)
                .EnumerateFiles(ShotFilePrefix + "*" + ShotFileSuffix, SearchOption.TopDirectoryOnly)
                .Where(f => IsOwnShotName(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)   // 最新在前
                .Skip(MaxRetainedShots)                        // 第 N 张之后 = 需要清理的
                .ToList();

            foreach (var f in stale)
            {
                try
                {
                    f.Delete();
                }
                catch (Exception ex)
                {
                    // v0.1.83 插件日志通道：Debug.WriteLine 在 Release 下等同静默，删除失败必须可观测。
                    Capability.System.Log(PluginLogLevel.Warn,
                        $"旧截图清理失败（保留 {MaxRetainedShots} 张）：{f.Name} - {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Capability.System.Log(PluginLogLevel.Warn, $"旧截图清理异常：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 严格匹配本插件命名模式 <c>shot_yyyyMMdd_HHmmss.png</c>（<c>shot_</c> + 8 位日期 + <c>_</c> + 6 位时间 + <c>.png</c>），
    /// 避免误删用户手动放入该目录的其它 png。
    /// </summary>
    private static bool IsOwnShotName(string name)
    {
        if (!name.StartsWith(ShotFilePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.EndsWith(ShotFileSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Length < ShotFilePrefix.Length + ShotFileSuffix.Length) return false;   // 防御：避免切片越界

        // 去掉前缀与扩展名后应恰为 "yyyyMMdd_HHmmss"（15 字符：8 位日期 + '_' + 6 位时间，全为数字）
        var core = name[ShotFilePrefix.Length..^ShotFileSuffix.Length];
        return core.Length == 15
            && core[8] == '_'
            && core.Remove(8, 1).All(char.IsAsciiDigit);
    }
}
