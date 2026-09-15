using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DesktopPet.App.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// 自动更新服务（v0.1.77·阶段1）——检查 → 提示 → 打开下载页，**不做静默安装**（BongoCat 同款最安全形态）。
///
/// <para>数据源：Gitee OpenAPI v5（<c>GET /repos/{owner}/{repo}/releases/latest</c>）。</para>
///
/// <para>设计要点（借鉴 BongoCat 的 <c>update_release.c</c> / <c>update_version.c</c>）：</para>
/// <list type="bullet">
///   <item>网络 / 解析 / 404 等任何异常一律 <b>吞掉并返回 null</b>（+ Log.Warn）——用户没网也绝不能崩宿主。</item>
///   <item>版本比较是纯函数（<see cref="IsNewer"/>），与网络解耦，可独立单测。</item>
///   <item>解析失败 → 保守返回 null（宁可漏报，不可误报）。</item>
/// </list>
/// </summary>
public static class UpdateService
{
    /// <summary>Gitee 仓库 owner。</summary>
    private const string Owner = "gddgssdfg";

    /// <summary>Gitee 仓库名。</summary>
    private const string Repo = "neko";

    /// <summary>「最新发布」接口地址（公开只读，无需 token）。</summary>
    public const string LatestReleaseApi =
        $"https://gitee.com/api/v5/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>更新说明截断长度（release body 可能很长，避免撑爆提示框）。</summary>
    private const int NotesMaxLength = 2000;

    /// <summary>HTTP 超时（秒）——无网时也不该无限挂起。</summary>
    private const int HttpTimeoutSeconds = 10;

    /// <summary>进程内复用的 HttpClient（不随每次检查重建，避免 socket 耗尽）。</summary>
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds) };
        // Gitee 对缺失 User-Agent 的请求可能返回 403，显式带上。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Neko-Updater/1.0");
        return http;
    }

    /// <summary>
    /// 当前程序集版本（三段式，如 "0.1.76"）。
    /// 从 <see cref="Assembly.GetExecutingAssembly"/> 的版本读——即宿主 Neko.dll 的 csproj &lt;Version&gt;。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>拉取「最新发布」。等价于 <see cref="FetchAsync"/> 命中官方接口。</summary>
    public static Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct) => FetchAsync(LatestReleaseApi, ct);

    /// <summary>
    /// 拉取并解析指定 URL 的「最新发布」响应。
    /// <para>任何异常（无网 / 超时 / 404 / JSON 损坏）→ 记 Warn 并返回 null，<b>绝不抛</b>。</para>
    /// </summary>
    /// <param name="url">Gitee releases/latest 接口地址（可显式传入以便测试失败路径）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<ReleaseInfo?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"[更新] 查询失败：HTTP {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseLatest(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;   // 调用方主动取消：静默
        }
        catch (Exception ex)
        {
            Log.Warn($"[更新] 查询异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析 Gitee <c>releases/latest</c> 的响应 JSON（纯函数，可单测）：
    /// <c>tag_name</c> / <c>body</c> / <c>html_url</c> / <c>assets[0].browser_download_url</c>。
    /// <para>字段缺失 / 非法 JSON / 非对象 → 返回 null。</para>
    /// </summary>
    public static ReleaseInfo? ParseLatest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) return null;

            // Gitee 的 releases/latest 响应**不含 html_url**（实测 schema：id/tag_name/name/body/assets...），
            // 故缺失时按仓库坐标自行拼「发布页」地址（点「打开下载页」用）。
            var releaseUrl = GetString(root, "html_url");
            if (string.IsNullOrWhiteSpace(releaseUrl))
                releaseUrl = $"https://gitee.com/{Owner}/{Repo}/releases/tag/{tag}";

            var notes = GetString(root, "body") ?? "";
            if (notes.Length > NotesMaxLength) notes = notes[..NotesMaxLength] + "…";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var u = GetString(a, "browser_download_url");
                    if (!string.IsNullOrWhiteSpace(u)) { downloadUrl = u; break; }
                }
            }

            return new ReleaseInfo
            {
                TagName = tag!,
                Version = NormalizeVersion(tag),
                Notes = notes,
                ReleaseUrl = releaseUrl,
                DownloadUrl = downloadUrl,
            };
        }
        catch (JsonException)
        {
            return null;   // JSON 损坏：保守返回 null
        }
    }

    /// <summary>
    /// 语义化版本比较：<paramref name="latest"/> 是否比 <paramref name="current"/> 新（纯函数，可单测）。
    /// <list type="bullet">
    ///   <item>忽略大小写与 <c>v</c> 前缀；按 <c>x.y.z</c> 数字段逐段比较（缺位补 0）。</item>
    ///   <item>预发布（带 <c>-</c> 后缀，如 <c>0.1.77-beta</c>）视为<b>比同号正式版旧</b>。</item>
    ///   <item>任一侧解析失败 → 保守返回 false（宁可漏报，不可误报）。</item>
    /// </list>
    /// </summary>
    public static bool IsNewer(string? current, string? latest)
    {
        if (!TryParseVersion(current, out var cur, out var curPre)) return false;
        if (!TryParseVersion(latest, out var lat, out var latPre)) return false;

        for (var i = 0; i < 3; i++)
        {
            if (lat[i] > cur[i]) return true;
            if (lat[i] < cur[i]) return false;
        }
        // 数字段完全相同：当前是预发布、目标为正式版 → 目标更新（真正的同号同性质则不更新）
        return curPre && !latPre;
    }

    // ------------------------------------------------------------------
    // 内部：解析
    // ------------------------------------------------------------------

    /// <summary>去掉前导 v/V 前缀（如 "v0.1.77" → "0.1.77"）。</summary>
    private static string NormalizeVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var t = tag.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        return t;
    }

    /// <summary>
    /// 解析版本字符串为三段数字 + 是否预发布。
    /// 支持 "1.2.3" / "v1.2.3" / "1.2"（补 0）/ "1.2.3-beta.1"（预发布）/ "1.2.3+build"（构建元数据）。
    /// 任一段非数字 / 空串 → 返回 false。
    /// </summary>
    private static bool TryParseVersion(string? s, out int[] nums, out bool isPre)
    {
        nums = new int[3];
        isPre = false;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var t = s.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];

        // 拆分核心版本号与 预发布(-) / 构建(+) 后缀
        var dash = t.IndexOf('-');
        var plus = t.IndexOf('+');
        var cut = -1;
        if (dash >= 0 && plus >= 0) cut = Math.Min(dash, plus);
        else if (dash >= 0 || plus >= 0) cut = Math.Max(dash, plus);
        if (cut >= 0)
        {
            isPre = dash >= 0 && (plus < 0 || dash < plus);   // 有 '-' 段才算预发布
            t = t[..cut];
        }

        var parts = t.Split('.');
        if (parts.Length == 0) return false;
        for (var i = 0; i < 3; i++)
        {
            if (i < parts.Length)
            {
                if (!int.TryParse(parts[i], out var n) || n < 0) return false;
                nums[i] = n;
            }
            else
            {
                nums[i] = 0;   // 缺位补 0（"1.2" == "1.2.0"）
            }
        }
        return true;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
