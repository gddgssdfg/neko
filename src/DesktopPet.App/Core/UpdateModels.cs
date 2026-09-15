namespace DesktopPet.App.Core;

/// <summary>
/// 一次「最新发布」查询的结果（v0.1.77 自动更新·阶段1：检查 → 提示 → 打开下载页，**不做静默安装**）。
/// <para>由 <c>DesktopPet.App.Services.UpdateService.FetchLatestAsync</c> 从 Gitee OpenAPI v5 解析而来。</para>
/// </summary>
public sealed class ReleaseInfo
{
    /// <summary>发布标签（Gitee 的 <c>tag_name</c>），如 "v0.1.77"。</summary>
    public string TagName { get; init; } = "";

    /// <summary>去掉 v 前缀的版本号（如 "0.1.77"），用于语义化比较。</summary>
    public string Version { get; init; } = "";

    /// <summary>更新说明（Gitee 的 <c>body</c>），已截断到约 2000 字。</summary>
    public string Notes { get; init; } = "";

    /// <summary>发布页地址（Gitee 的 <c>html_url</c>），用于「打开下载页」。</summary>
    public string ReleaseUrl { get; init; } = "";

    /// <summary>首个资产的下载直链（<c>assets[0].browser_download_url</c>）；发布无附件时为 null。</summary>
    public string? DownloadUrl { get; init; }
}
