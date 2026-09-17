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
/// </summary>
public sealed class ScreenshotPlugin : PluginBase
{
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
                Pet.Say($"截图已保存：{path}", 4000);   // 宿主侧已封送到 UI 线程，安全
            }
            catch (Exception ex)
            {
                Pet.Say($"截图失败：{ex.Message}", 3000);
            }
        });
    }
}
