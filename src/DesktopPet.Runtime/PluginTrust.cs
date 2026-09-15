using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopPet.Runtime;

/// <summary>
/// 内置插件信任集（v0.1.80 / 审计 P0-4 T02）：
/// 宿主随包携带一份清单（默认 <see cref="DefaultFileName"/>，置于 exe 同级，不在 plugins/ 内），
/// 为「随包发布的官方内置插件」开一条<b>受控信任通道</b> —— 只有 id 在清单内
/// （且清单登记了内容摘要时，现场 manifest / 入口 dll 的 SHA-256 与登记值一致）的插件，才被放行其高风险权限声明。
///
/// <para><b>安全底线</b>：第三方插件不在清单内 → 仍被拒载；清单缺失 / 损坏 / schema 不符 → 返回
/// <see cref="Empty"/>（一律不信任），行为与旧版完全一致，不引入任何新风险。</para>
/// </summary>
public sealed class PluginTrustSet
{
    /// <summary>默认清单文件名。放 App 根（exe 同级），不在 plugins/ 内——避免被插件自写篡改。</summary>
    public const string DefaultFileName = "builtins.lock.json";

    /// <summary>当前唯一支持的清单 schema 版本。</summary>
    private const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, TrustEntry> _entries;

    private PluginTrustSet(Dictionary<string, TrustEntry> entries) => _entries = entries;

    /// <summary>无清单时的安全兜底：一律不信任。</summary>
    public static PluginTrustSet Empty { get; } =
        new(new Dictionary<string, TrustEntry>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 从 <paramref name="lockFilePath"/> 载入信任清单。
    /// 缺失文件 / 坏 JSON / 不认识的 schema → 返回 <see cref="Empty"/> 并给出 <paramref name="warning"/>，
    /// <b>绝不抛异常</b>（打包/部署缺文件不能拖垮宿主启动）。
    /// </summary>
    public static PluginTrustSet Load(string lockFilePath, out string? warning)
    {
        warning = null;
        try
        {
            if (string.IsNullOrWhiteSpace(lockFilePath) || !File.Exists(lockFilePath))
            {
                warning = $"未找到内置插件信任清单「{lockFilePath}」，按「一律不信任」处理";
                return Empty;
            }

            var model = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(lockFilePath), JsonOpts);
            if (model is null)
            {
                warning = "信任清单内容为空，按「一律不信任」处理";
                return Empty;
            }
            if (model.Schema != SupportedSchema)
            {
                warning = $"不认识的信任清单 schema={model.Schema}（期望 {SupportedSchema}），按「一律不信任」处理";
                return Empty;
            }

            var map = new Dictionary<string, TrustEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in model.Builtins)
            {
                if (string.IsNullOrWhiteSpace(b.Id)) continue;
                map[b.Id.Trim()] = new TrustEntry
                {
                    ManifestSha256 = Normalize(b.ManifestSha256),
                    EntrySha256 = Normalize(b.EntrySha256),
                };
            }
            return new PluginTrustSet(map);
        }
        catch (Exception ex)
        {
            warning = $"信任清单加载失败（按「一律不信任」处理）: {ex.Message}";
            return Empty;
        }
    }

    /// <summary>
    /// 判定 <paramref name="id"/> 是否受信：id 必须在清单内；若清单为该 id 登记了摘要，
    /// 则现场 <paramref name="manifestPath"/> / <paramref name="entryPath"/> 的 SHA-256 必须匹配。
    /// <paramref name="reason"/> 为人类可读的判定依据（供加载错误信息展示）。
    /// </summary>
    public bool IsTrusted(string id, string manifestPath, string entryPath, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(id) || !_entries.TryGetValue(id, out var entry))
        {
            reason = $"插件 '{id}' 不在内置信任清单内";
            return false;
        }

        try
        {
            if (entry.ManifestSha256 is { Length: > 0 } mHash &&
                !DigestEquals(mHash, Sha256Hex(manifestPath)))
            {
                reason = $"manifest 摘要不匹配（清单 {Short(mHash)}，现场 {Short(Sha256Hex(manifestPath))}）";
                return false;
            }
            if (entry.EntrySha256 is { Length: > 0 } eHash &&
                !DigestEquals(eHash, Sha256Hex(entryPath)))
            {
                reason = $"入口 dll 摘要不匹配（清单 {Short(eHash)}，现场 {Short(Sha256Hex(entryPath))}）";
                return false;
            }
        }
        catch (Exception ex)
        {
            // 读文件/算摘要失败 → 保守视为不可信（绝不因异常而放行）
            reason = $"摘要校验失败（保守视为不可信）: {ex.Message}";
            return false;
        }

        var bound = entry.ManifestSha256 is { Length: > 0 } || entry.EntrySha256 is { Length: > 0 };
        reason = bound ? "命中内置信任清单且内容摘要匹配" : "命中内置信任清单（清单未登记摘要，仅按 id 信任）";
        return true;
    }

    // ---------------- 内部：摘要计算与规范化 ----------------

    /// <summary>规范化摘要字符串：去空白、容忍 <c>sha256:</c> 前缀、统一小写。</summary>
    private static string? Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        var idx = t.IndexOf(':');
        if (idx >= 0) t = t[(idx + 1)..];
        t = t.Trim();
        return t.Length == 0 ? null : t.ToLowerInvariant();
    }

    private static bool DigestEquals(string expectedLower, string actualLower)
        => string.Equals(expectedLower, actualLower, StringComparison.OrdinalIgnoreCase);

    private static string Short(string hex) => hex.Length <= 12 ? hex : hex[..12] + "…";

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---------------- 清单 JSON 模型 ----------------

    private sealed class TrustEntry
    {
        public string? ManifestSha256 { get; init; }
        public string? EntrySha256 { get; init; }
    }

    private sealed class LockFile
    {
        public int Schema { get; init; }
        public List<LockedPlugin> Builtins { get; init; } = new();
    }

    private sealed class LockedPlugin
    {
        public string Id { get; init; } = "";
        public string? ManifestSha256 { get; init; }
        public string? EntrySha256 { get; init; }
    }
}
