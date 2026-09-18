namespace DesktopPet.Core;

/// <summary>
/// 插件日志级别（v0.1.83 新增，仅为 <see cref="ISystem.Log"/> 服务）。
/// <para>与宿主内部日志级别的映射由宿主实现决定：Debug→Debug、Info→Info、Warn→Warn、Error→Error。
/// 宿主默认级别为 <c>warn</c>，因此 <see cref="Debug"/>/<see cref="Info"/> 默认<b>不落盘</b>，
/// 只有用户显式把 config.json 的 <c>logLevel</c> 调成 debug/info 才会记录。</para>
/// </summary>
public enum PluginLogLevel
{
    /// <summary>调试细节（默认不落盘）。</summary>
    Debug,
    /// <summary>一般信息（默认不落盘）。</summary>
    Info,
    /// <summary>警告：可恢复但需要留痕的失败（默认落盘）。</summary>
    Warn,
    /// <summary>错误：真实故障（默认落盘，且会附异常堆栈）。</summary>
    Error,
}
