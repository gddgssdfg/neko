using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.TodoList;

/// <summary>待办条目（可 JSON 序列化存 Storage）</summary>
public sealed class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// 待办清单插件：右键菜单「待办清单」打开窗口，增删改勾选都实时持久化到 Storage。
/// 展示 List<T> 类型 Storage 存储（System.Text.Json 序列化）。
/// </summary>
public sealed class TodoListPlugin : PluginBase
{
    public const string TodoKey = "todos";
    public const int MaxItems = 30;

    public override string Id => "com.lantern.todo";
    public override string Name => "待办清单";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.UIRegister | PluginPermissions.Storage;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "todo-open",
            Label = "待办清单",
            Icon = "todo",
            Order = 21,
            OnInvoke = OpenTodo,
        });
    }

    private void OpenTodo()
    {
        var list = Capability.Storage.Get<List<TodoItem>>(TodoKey) ?? new List<TodoItem>();
        new TodoWindow(list, save: Save).Show();
    }

    private void Save(List<TodoItem> list)
    {
        if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
        Capability.Storage.Set(TodoKey, list);
    }
}
