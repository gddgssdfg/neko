using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DesktopPet.Builtin.Bazi;

/// <summary>一条对话消息（同时用于请求体与本地历史持久化）。</summary>
public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage OfSystem(string content) => new("system", content);
    public static ChatMessage OfUser(string content) => new("user", content);
    public static ChatMessage OfAssistant(string content) => new("assistant", content);
}

/// <summary>AI 调用结果。失败时 <see cref="Error"/> 是给用户看的可读文案，<b>绝不抛异常</b>。</summary>
public readonly record struct ChatResult(bool Ok, string Text, string? Error)
{
    public static ChatResult Success(string text) => new(true, text ?? "", null);
    public static ChatResult Failure(string error) => new(false, "", error);
}

/// <summary>
/// DeepSeek Chat Completions 客户端。
/// <para><b>HttpClient 用 static readonly 单例</b>（规格第 7 节点名批评了 weather 插件「每次 new」耗尽 socket 的反模式）。</para>
/// <para>任何失败（无 Key / 断网 / 超时 / 非 2xx / 解析失败）都折成 <see cref="ChatResult.Failure"/> 返回，
/// <b>不向宿主抛异常</b>（手册铁律 7）。全部调用为 async，绝不阻塞 UI 线程。</para>
/// </summary>
public static class DeepSeekClient
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    private const string ModelName = "deepseek-chat";

    /// <summary>
    /// 进程内唯一 HttpClient（连接池复用，避免 socket 耗尽）。
    /// <para>⚠️ <b>v0.1.82：改为可释放</b>。插件程序集跑在**可回收 ALC** 里，而 SocketsHttpHandler 的
    /// 连接池清理定时器会从**进程级根**钉住这个类型 → 整个 ALC 收不掉。
    /// 实测：一次 load/unload 残留 <b>~8.5MB</b>，且跳过 OnLoad 就能回收 →
    /// 根确实是 OnLoad 期间建立的（本插件的自检会首次触碰这个单例）。
    /// 现在由 <see cref="DisposeShared"/> 在插件 OnUnload 时释放。</para>
    /// </summary>
    private static readonly object HttpLock = new();
    private static HttpClient? _http;

    private static HttpClient Http
    {
        get
        {
            if (_http is not null) return _http;
            lock (HttpLock) return _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }
    }

    /// <summary>v0.1.82：释放共享 HttpClient。<b>必须由插件 OnUnload 调用</b>，否则 ALC 无法回收。</summary>
    public static void DisposeShared()
    {
        try { _http?.Dispose(); } catch { /* 释放失败不该拖垮卸载 */ }
        _http = null;
    }

    /// <summary>最近一次 <see cref="SelfCheck"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>校验 API Key；返回 null 表示可用，否则返回给用户看的可读错误文案。</summary>
    public static string? ValidateApiKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? "尚未配置 DeepSeek API Key，请在「命理助手」设置页填写后再试。"
            : null;

    /// <summary>向 DeepSeek 发起一次对话补全。失败返回可读错误，不抛异常。</summary>
    public static async Task<ChatResult> ChatAsync(string? apiKey, IReadOnlyList<ChatMessage> messages)
    {
        var keyError = ValidateApiKey(apiKey);
        if (keyError is not null) return ChatResult.Failure(keyError);

        if (messages is null || messages.Count == 0)
            return ChatResult.Failure("没有可发送的内容。");

        try
        {
            var body = BuildRequestBody(messages);
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey!.Trim());
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return ChatResult.Failure(DescribeHttpError(resp.StatusCode, json));

            var text = ExtractContent(json);
            return string.IsNullOrWhiteSpace(text)
                ? ChatResult.Failure("AI 没有返回内容，请稍后再试。")
                : ChatResult.Success(text);
        }
        catch (TaskCanceledException)
        {
            // HttpClient 超时在 .NET 中表现为 TaskCanceledException
            return ChatResult.Failure("AI 请求超时（30 秒）。请检查网络后重试。");
        }
        catch (HttpRequestException)
        {
            return ChatResult.Failure("网络连接失败。请检查网络后重试。");
        }
        catch (Exception ex)
        {
            return ChatResult.Failure($"AI 请求出错：{ex.Message}");
        }
    }

    /// <summary>组装 OpenAI 兼容的请求体 JSON（纯函数，供自检断言结构）。</summary>
    /// <remarks>
    /// v0.1.82（P0 修复）：<b>不再</b>用 <c>JsonSerializer.Serialize</c> 序列化一个定义在插件程序集内的匿名类型，
    /// 改为用 <see cref="Utf8JsonWriter"/> 手写请求体。
    /// <para><b>为什么</b>：System.Text.Json 的反射式序列化路径会把「插件自定义类型」的元数据写入
    /// <b>进程级共享缓存</b>，从而从默认 ALC 侧强引用该类型 → 插件卸载后整个可回收 ALC 收不回来
    /// （实测每次 load/unload 残留 ~8.9MB）。换 options / 换 resolver 均无效，唯有绕开 STJ 反射。</para>
    /// <para>本方法产出的 JSON 与原实现<b>逐字节等价</b>：同样的字段名、字段顺序、紧凑格式与默认转义。</para>
    /// </remarks>
    internal static string BuildRequestBody(IReadOnlyList<ChatMessage> messages)
    {
        // 默认 Utf8JsonWriter 的编码器 = JavaScriptEncoder.Default，与 JsonSerializer.Serialize 默认一致，
        // 因此非 ASCII（中文）与需要转义的字符处理结果完全相同。
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", ModelName);

            writer.WriteStartArray("messages");
            foreach (var m in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", m.Role);
                writer.WriteString("content", m.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteBoolean("stream", false);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>从响应 JSON 里取 <c>choices[0].message.content</c>；解析失败返回 null。</summary>
    private static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;
            var msg = choices[0].TryGetProperty("message", out var m) ? m : default;
            return msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c)
                ? c.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>HTTP 非 2xx → 给用户看的可读文案（401/402/429/5xx 分别提示）。</summary>
    private static string DescribeHttpError(HttpStatusCode code, string body)
    {
        var detail = ExtractApiError(body);
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $"（{detail}）";
        return code switch
        {
            HttpStatusCode.Unauthorized => "API Key 无效或已过期（401），请在设置页检查后重试。" + suffix,
            HttpStatusCode.PaymentRequired => "DeepSeek 账户余额不足（402），请充值后重试。" + suffix,
            HttpStatusCode.TooManyRequests => "请求过于频繁（429），请稍后再试。" + suffix,
            (HttpStatusCode)500 or (HttpStatusCode)502 or (HttpStatusCode)503 =>
                "AI 服务暂时不可用，请稍后再试。" + suffix,
            _ => $"AI 请求失败（HTTP {(int)code}）。" + suffix,
        };
    }

    /// <summary>尝试从错误响应里抽出 <c>error.message</c>（尽力而为，失败返回空串）。</summary>
    private static string ExtractApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var m))
                return m.GetString() ?? "";
        }
        catch { /* 尽力而为地抽取 error.message：解析失败返回空串，调用方另有兜底文案 */ }
        return "";
    }

    // ---- 自检（纯函数 + 一次无网络的失败路径验证；供加载器回归读取） ----

    /// <summary>
    /// 自检：① 空 / 空白 Key 必须返回可读错误而非抛异常；② 请求体结构含 model/role/content/stream。
    /// <para>空 Key 分支在发网络请求之前就返回（任务同步完成），故 <c>GetAwaiter().GetResult()</c> 不会死锁。</para>
    /// </summary>
    public static string SelfCheck()
    {
        var failures = new List<string>();
        try
        {
            var r1 = ChatAsync(null, new[] { ChatMessage.OfUser("你好") }).GetAwaiter().GetResult();
            if (r1.Ok) failures.Add("空 Key 竟返回成功");
            if (string.IsNullOrWhiteSpace(r1.Error)) failures.Add("空 Key 未给出可读错误");

            var r2 = ChatAsync("   ", new[] { ChatMessage.OfUser("你好") }).GetAwaiter().GetResult();
            if (r2.Ok) failures.Add("空白 Key 竟返回成功");

            var body = BuildRequestBody(new[] { ChatMessage.OfSystem("sys"), ChatMessage.OfUser("hi") });
            if (!body.Contains("deepseek-chat")) failures.Add("请求体缺 model");
            if (!body.Contains("\"role\"") || !body.Contains("\"content\"")) failures.Add("请求体缺 role/content");
            if (!body.Contains("\"stream\":false")) failures.Add("请求体缺 stream=false");
        }
        catch (Exception ex)
        {
            failures.Add($"自检抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        LastSelfTest = failures.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", failures);
        return LastSelfTest;
    }
}
