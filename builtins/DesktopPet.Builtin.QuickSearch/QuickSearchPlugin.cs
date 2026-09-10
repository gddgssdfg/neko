using System.Web;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.QuickSearch;

/// <summary>
/// 快捷搜索插件：复制文字 → 右键菜单「搜索」→ 读剪贴板 → 打开浏览器百度搜索。
/// 展示 ISystem.ReadClipboard + OpenUrl 组合。
/// </summary>
public sealed class QuickSearchPlugin : PluginBase
{
    public override string Id => "com.lantern.search";
    public override string Name => "快捷搜索";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister | PluginPermissions.Clipboard;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "search-clip",
            Label = "搜索剪贴板",
            Icon = "gear",
            Order = 27,
            OnInvoke = SearchClipboard,
        });
    }

    private void SearchClipboard()
    {
        try
        {
            var text = Capability.System.ReadClipboard()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                Pet.Say("先复制要搜索的内容，再点我哦", 2500);
                return;
            }
            Pet.Say($"帮你搜「{Truncate(text, 12)}」", 2000);
            Capability.System.OpenUrl("https://www.baidu.com/s?wd=" + HttpUtility.UrlEncode(text));
        }
        catch
        {
            Pet.Say("搜索失败", 2000);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
