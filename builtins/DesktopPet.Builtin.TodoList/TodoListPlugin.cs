using System.Text.Json;
using System.Text.Json.Serialization;
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
        var list = Load();
        new TodoWindow(list, save: Save, system: Capability.System, focusTask: title =>
        {
            var request = new FocusTaskRequest(title);
            Capability.Events.Emit(CompanionEvents.FocusTaskRequested, request);
            return request.Message;
        }).Show();
    }

    private void Save(List<TodoItem> list)
    {
        if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
        try
        {
            // v0.1.82（P0 / ALC 钉根收口）：改为**插件侧**用源生成序列化成字符串再交宿主持久化。
            // 原 Capability.Storage.Set<List<TodoItem>>(TodoKey, list) 把插件类型交给宿主
            // JsonPluginStorage 做 STJ 反射序列化 → 钉住可回收 ALC（AlcPinProbe: todolistsave = PINNED(N)）。
            Capability.Storage.Set(TodoKey, JsonSerializer.Serialize(list, TodoJsonContext.Default.ListTodoItem));
        }
        catch
        {
            // 持久化失败不拖垮宿主（手册铁律 7）
        }
    }

    /// <summary>
    /// 读取待办列表。v0.1.82：新格式 = 插件侧序列化出的 JSON 字符串（不含插件类型反射）；
    /// 同时向后兼容旧格式——旧版把 <c>List&lt;TodoItem&gt;</c> 直接经宿主泛型存储落盘，文件是裸 JSON 数组。
    /// </summary>
    private List<TodoItem> Load()
    {
        try
        {
            var json = Capability.Storage.Get<string>(TodoKey);
            if (!string.IsNullOrWhiteSpace(json))
                return JsonSerializer.Deserialize(json, TodoJsonContext.Default.ListTodoItem) ?? new List<TodoItem>();

            // 旧格式兼容：裸数组用 JsonElement（BCL 类型，不钉插件 ALC）读出，再用源生成上下文反序列化。
            var legacy = Capability.Storage.Get<JsonElement>(TodoKey);
            if (legacy.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize(legacy.GetRawText(), TodoJsonContext.Default.ListTodoItem)
                       ?? new List<TodoItem>();
        }
        catch
        {
            // 数据损坏 → 当作空清单（铁律 7）
        }
        return new List<TodoItem>();
    }
}

/// <summary>
/// v0.1.82（P0 / ALC 钉根收口）：<see cref="TodoItem"/> 列表的**源生成**序列化上下文。
/// <para>取代宿主泛型存储对插件类型的 STJ 反射序列化；上下文类型随插件 ALC 一起卸载，不产生跨 ALC 根。</para>
/// </summary>
[JsonSerializable(typeof(List<TodoItem>))]
internal partial class TodoJsonContext : JsonSerializerContext
{
}
