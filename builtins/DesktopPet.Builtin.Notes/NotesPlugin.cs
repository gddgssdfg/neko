using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Notes;

/// <summary>
/// 备忘录插件（dogfooding 示范）：右键菜单注册"备忘录"槽位，
/// 打开桌面便签窗口，内容经 Storage 持久化到 data/plugins/com.lantern.notes/。
/// </summary>
public sealed class NotesPlugin : PluginBase
{
    public const string NotesKey = "notes";

    public override string Id => "com.lantern.notes";
    public override string Name => "备忘录";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.UIRegister | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "notes-open",
            Label = "备忘录",
            Icon = "home",
            Order = 5,
            OnInvoke = OpenNotes,
        });
    }

    private void OpenNotes()
    {
        var win = new NotesWindow(
            current: Capability.Storage.Get<string>(NotesKey) ?? "",
            onSave: text => Capability.Storage.Set(NotesKey, text));
        win.Show();
    }
}
