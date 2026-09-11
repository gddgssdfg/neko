using System.Text.Json;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 对话历史持久化（规格 4.4）：只保留最近 <see cref="MaxTurns"/> 轮（用户 + AI 各一条为一轮）。
/// 存 <see cref="IPluginStorage"/> 的 <see cref="StorageKey"/>，滚动截断。读写均不抛异常。
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
            var list = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
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
            storage.Set(StorageKey, JsonSerializer.Serialize(list));
        }
        catch
        {
            // 持久化失败不拖垮宿主（铁律 7）
        }
    }
}
