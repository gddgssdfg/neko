using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// API Key 的本地加密（DPAPI，<see cref="DataProtectionScope.CurrentUser"/> 作用域）。
/// <para>规格第 7 节：<c>Capability.Storage</c> 是明文 JSON，Key 不能明文落盘。</para>
/// <para>存储格式：<c>enc:&lt;Base64(DPAPI 密文)&gt;</c>。<b>向后兼容</b>：读取时若无 <c>enc:</c> 前缀，
/// 视为旧版明文原样返回；写回时一律加密。</para>
/// <para>加密/解密失败均<b>不抛异常</b>：加密失败降级明文（避免丢数据），解密失败返回 null（要求用户重填）。</para>
/// </summary>
public static class ApiKeyProtector
{
    private const string Prefix = "enc:";

    /// <summary>附加熵：换用户 / 换机器即无法解开（DPAPI CurrentUser 语义），进一步降低被复用风险。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("neko.bazi.apikey.v1");

    /// <summary>最近一次 <see cref="SelfCheck"/> 的结果（"OK" 或 "FAIL: ..."）。供加载器回归读取。</summary>
    public static string LastSelfTest { get; private set; } = "未运行";

    /// <summary>加密为存储串（带 <c>enc:</c> 前缀）。空串返回空串；失败降级为明文（绝不抛）。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch
        {
            return plain;   // 极罕见（非 Windows / CSP 受限）→ 降级明文，宁可不加密也不丢数据
        }
    }

    /// <summary>
    /// 解密存储串为明文。旧版明文（无前缀）原样返回；<c>enc:</c> 密文解密失败返回 null（视为无效，要求重填）。
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;   // 向后兼容：旧版明文

        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var dec = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch
        {
            return null;   // 换机器 / 数据损坏 → 解不开
        }
    }

    // ---- 自检（DPAPI 往返 + 向后兼容；供加载器回归读取） ----

    /// <summary>
    /// 自检：加密往返能还原、密文带 <c>enc:</c> 前缀且不含明文、旧版明文原样兼容、null / 损坏密文安全返回。
    /// </summary>
    public static string SelfCheck()
    {
        var failures = new List<string>();
        try
        {
            const string sample = "sk-selftest-1234567890";
            var enc = Protect(sample);
            if (!enc.StartsWith(Prefix, StringComparison.Ordinal)) failures.Add("加密后缺 enc: 前缀");
            if (enc.Contains(sample, StringComparison.Ordinal)) failures.Add("密文仍含明文");
            if (Unprotect(enc) != sample) failures.Add("解密未还原明文");
            if (Unprotect(sample) != sample) failures.Add("旧版明文未兼容");
            if (Unprotect(null) is not null) failures.Add("null 未返回 null");
            if (Unprotect("enc:@@not-base64@@") is not null) failures.Add("损坏密文未返回 null");
        }
        catch (Exception ex)
        {
            failures.Add($"自检抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        LastSelfTest = failures.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", failures);
        return LastSelfTest;
    }
}

/// <summary>
/// JSON 转换器：让 <see cref="BaziProfile.ApiKey"/> 在序列化时自动加密、反序列化时自动解密。
/// 这样 <c>BaziProfile</c> 内存里始终是明文，落盘的是密文，对使用方完全透明。
/// </summary>
public sealed class EncryptedApiKeyConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return ApiKeyProtector.Unprotect(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value)) writer.WriteNullValue();
        else writer.WriteStringValue(ApiKeyProtector.Protect(value));
    }
}
