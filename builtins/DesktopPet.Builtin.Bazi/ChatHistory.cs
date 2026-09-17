using System.IO;
using System.Text;
using System.Text.Json;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 对话历史持久化（规格 4.4）：只保留最近 <see cref="MaxTurns"/> 轮（用户 + AI 各一条为一轮）。
/// 存 <see cref="IPluginStorage"/> 的 <see cref="StorageKey"/>，滚动截断。读写均不抛异常。
/// <para><b>v0.1.82（P0 同源修复）</b>：原实现用 <c>JsonSerializer.Serialize/Deserialize&lt;List&lt;ChatMessage&gt;&gt;</c>
/// —— <see cref="ChatMessage"/> 是<b>插件程序集里的类型</b>，STJ 的反射路径会把该类型的元数据写进
/// <b>进程级共享缓存</b>，从而钉住整个可回收 ALC（首次 AI 对话即泄漏 ~8.9MB，与 DeepSeekClient 同源）。
/// 现改为手写 <see cref="Utf8JsonWriter"/> 写、<see cref="JsonDocument"/> 读——两者均<b>不触碰任何
/// plugin 类型元数据</b>，ALC 可正常回收；JSON 形态与原先 STJ 默认输出<b>逐字节一致</b>（PascalCase
/// 属性名、紧凑、<see cref="System.Text.Encodings.Web.JavaScriptEncoder"/> 默认转义），旧存档仍可读。</para>
/// </summary>
public static class ChatHistory
{
    /// <summary>Storage 键名。</summary>
    public const string StorageKey = "chatHistory";

    /// <summary>保留最近轮数（规格建议 10 轮）。</summary>
    public const int MaxTurns = 10;

    /// <summary>保留消息条数上限（10 轮 = 20 条）。</summary>
    public const int MaxMessages = MaxTurns * 2;

    /// <summary>读取最近历史（仅 user/assistant，最多 <see cref="MaxMessages"/> 条）；失败返回空列表。</summary>
    public static List<ChatMessage> Load(IPluginStorage storage)
    {
        try
        {
            var json = storage.Get<string>(StorageKey);
            if (string.IsNullOrWhiteSpace(json)) return new List<ChatMessage>();

            // 手写解析（JsonDocument 是纯读取器，不经过反射、不缓存类型元数据）——
            // 属性名与 STJ 默认一致为 PascalCase（Role/Content）。
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new List<ChatMessage>();

            var list = new List<ChatMessage>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var role = el.TryGetProperty("Role", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() : null;
                var content = el.TryGetProperty("Content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                if (role is null || content is null) continue;
                list.Add(new ChatMessage(role, content));
            }

            return list
                .Where(m => (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrEmpty(m.Content))
                .TakeLast(MaxMessages)
                .ToList();
        }
        catch
        {
            return new List<ChatMessage>();
        }
    }

    /// <summary>追加一轮问答并滚动截断后写回；失败静默。</summary>
    public static void Append(IPluginStorage storage, string question, string answer)
    {
        try
        {
            var list = Load(storage);
            list.Add(ChatMessage.OfUser(question));
            list.Add(ChatMessage.OfAssistant(answer));
            if (list.Count > MaxMessages)
                list = list.Skip(list.Count - MaxMessages).ToList();
            storage.Set(StorageKey, Serialize(list));
        }
        catch
        {
            // 持久化失败不拖垮宿主（铁律 7）
        }
    }

    /// <summary>
    /// 手写序列化 <c>[{"Role":"...","Content":"..."}, ...]</c>（紧凑、PascalCase），
    /// 与旧版 <c>JsonSerializer.Serialize(list)</c> 逐字节一致，且不触发 STJ 反射缓存。
    /// </summary>
    private static string Serialize(List<ChatMessage> list)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var m in list)
            {
                writer.WriteStartObject();
                writer.WriteString("Role", m.Role);
                writer.WriteString("Content", m.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
