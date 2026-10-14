using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DesktopPet.App.Core;

namespace DesktopPet.App.Services;

/// <summary>
/// 自动更新服务（v0.1.77·阶段1）——检查 → 提示 → 打开下载页，**不做静默安装**（BongoCat 同款最安全形态）。
///
/// <para>数据源：Gitee OpenAPI v5，**两级**（v0.2.2）——
/// <c>GET /repos/{owner}/{repo}/releases/latest</c>（有 Release 对象时信息最全：说明 + 下载链接）
/// → 回落 <c>GET /repos/{owner}/{repo}/tags</c>（本项目发布流程只**打 tag**，取最大语义化版本）。
/// 只支持**公开仓库**：私有仓库的 API 一律 404 `Not Found Project`，而客户端不该内置 token。</para>
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

    /// <summary>
    /// 「列出所有 tag」接口（v0.2.2 新增；每页 100，够用）。
    /// <para>为什么要它：本项目发布流程是**打 tag**，未必会创建 Gitee **Release 对象** ——
    /// 而 <c>releases/latest</c> 返回的是 Release 对象，没有 Release 时直接 404，
    /// 于是"检查更新"永远查不到东西（v0.2.2 实测确认过这个形态）。</para>
    /// </summary>
    public const string TagsApi =
        $"https://gitee.com/api/v5/repos/{Owner}/{Repo}/tags?per_page=100";

    /// <summary>仓库主页（检查失败时「手动查看」用；永远可达，不依赖是否有 Release / tag）。</summary>
    public const string RepoUrl = $"https://gitee.com/{Owner}/{Repo}";

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

    /// <summary>
    /// 拉取「最新版本」。**两级数据源**（v0.2.2）：
    /// <list type="number">
    ///   <item><see cref="LatestReleaseApi"/> —— 有 Release 对象时信息最全（更新说明 + 下载链接）；</item>
    ///   <item>回落 <see cref="TagsApi"/> —— 只有 tag（本项目当前形态）时，取 tag 列表中的**最大语义化版本**。</item>
    /// </list>
    /// 两级都失败 → 返回 null（**绝不抛**）。
    /// </summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct)
    {
        // ① Releases 优先。**静默**：没有 Release 是常态（我们只打 tag），不该每天刷一条 404 警告。
        var release = await FetchAsync(LatestReleaseApi, ct, quietOnFailure: true).ConfigureAwait(false);
        if (release is not null) return release;

        // ② 回落到 tag 列表
        var json = await GetJsonAsync(TagsApi, ct).ConfigureAwait(false);
        return json is null ? null : ParseLatestTags(json);
    }

    /// <summary>
    /// 拉取并解析指定 URL 的「最新发布」响应。
    /// <para>任何异常（无网 / 超时 / 404 / JSON 损坏）→ 记 Warn 并返回 null，<b>绝不抛</b>。</para>
    /// </summary>
    /// <param name="url">Gitee releases/latest 接口地址（可显式传入以便测试失败路径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="quietOnFailure">
    /// 失败时不写日志（v0.2.2）：用于"多级数据源"里的**非末级**尝试 ——
    /// 否则「没有 Release」这种常态会伪装成故障刷日志。
    /// </param>
    public static async Task<ReleaseInfo?> FetchAsync(string url, CancellationToken ct, bool quietOnFailure = false)
    {
        var json = await GetJsonAsync(url, ct, quietOnFailure).ConfigureAwait(false);
        return json is null ? null : ParseLatest(json);
    }

    /// <summary>
    /// GET 一个 JSON 接口并按字符串返回正文；失败记 Warn（可静默）并返回 null，**绝不抛**。
    /// </summary>
    private static async Task<string?> GetJsonAsync(string url, CancellationToken ct, bool quietOnFailure = false)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (!quietOnFailure) Log.Warn($"[更新] 查询失败：HTTP {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;   // 调用方主动取消：静默
        }
        catch (Exception ex)
        {
            if (!quietOnFailure) Log.Warn($"[更新] 查询异常：{ex.Message}");
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
    /// 解析「列出所有 tag」（<see cref="TagsApi"/>）的响应 JSON，取其中的**最大语义化版本**。
    ///
    /// <para>Gitee 响应是数组：<c>[{ "name": "v0.1.0", "commit": { "sha": ..., "date": ... }, ... }, ...]</c>
    /// （结构依官方文档核对：每项的 tag 名在 <c>name</c>）。</para>
    ///
    /// <para><b>刻意不依赖接口返回顺序</b>：逐个用 <see cref="CompareVersions"/> 比数字段取最大 ——
    /// 字符串序会把 <c>0.1.9</c> 排到 <c>0.1.10</c> 之后，那是错的。</para>
    ///
    /// <para>非版本号 tag（如 <c>nightly</c>）一律跳过；空数组 / 非数组 / JSON 损坏 / 无合法版本 → null。</para>
    /// </summary>
    public static ReleaseInfo? ParseLatestTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return null;

            int[]? best = null;
            var bestPre = false;
            string? bestTag = null;

            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!TryParseVersion(name, out var nums, out var isPre)) continue;   // 非版本号 tag：跳过

                if (best is null || CompareVersions(nums, isPre, best, bestPre) > 0)
                {
                    best = nums;
                    bestPre = isPre;
                    bestTag = name;
                }
            }

            if (bestTag is null) return null;

            return new ReleaseInfo
            {
                TagName = bestTag,
                Version = NormalizeVersion(bestTag),
                Notes = "",                                              // tag 无更新说明（Release 才有）
                ReleaseUrl = $"{RepoUrl}/tags",                          // tag 来源 → 指向 tag 列表页
                DownloadUrl = null,
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
        return CompareVersions(cur, curPre, lat, latPre) < 0;
    }

    /// <summary>
    /// 三段版本比较（v0.2.2 抽出，供 <see cref="IsNewer"/> 与 <see cref="ParseLatestTags"/> 共用，
    /// 避免"同一套比较逻辑写两遍、改一处漏一处"）。
    /// a 比 b 新 → <c>+1</c>；旧 → <c>-1</c>；同号同性质 → <c>0</c>；<b>预发布视为比同号正式版旧</b>。
    /// </summary>
    private static int CompareVersions(int[] a, bool aPre, int[] b, bool bPre)
    {
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i] ? 1 : -1;
        }
        if (aPre == bPre) return 0;
        return aPre ? -1 : 1;
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
