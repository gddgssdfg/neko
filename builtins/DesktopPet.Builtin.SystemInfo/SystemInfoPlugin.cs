using System.IO;
using System.Runtime.InteropServices;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.SystemInfo;

/// <summary>
/// 系统信息插件：P/Invoke 读 CPU/内存，DriveInfo 读磁盘，猫气泡播报。
/// 展示插件可直接 P/Invoke 系统 API（无需宿主能力接口）。
/// </summary>
public sealed class SystemInfoPlugin : PluginBase
{
    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    public override string Id => "com.lantern.sysinfo";
    public override string Name => "系统信息";

    public override PluginPermissions RequiredPermissions =>
        PluginPermissions.PetAnimate | PluginPermissions.UIRegister;

    public override void OnLoad(IPluginContext ctx)
    {
        base.OnLoad(ctx);

        UI.RegisterRadialSlot(new RadialSlot
        {
            Id = "sys-info",
            Label = "系统信息",
            Icon = "info",
            Order = 20,
            OnInvoke = () => Show(),
        });
    }

    private async void Show()
    {
        try
        {
            var cpu = await GetCpuUsageAsync();

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            GlobalMemoryStatusEx(ref mem);
            var totalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
            var availGb = mem.ullAvailPhys / 1024.0 / 1024 / 1024;
            var usedGb = totalGb - availGb;

            var disk = "磁盘 ?";
            var c = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed);
            if (c is not null && c.TotalSize > 0)
            {
                var used = c.TotalSize - c.AvailableFreeSpace;
                disk = $"C盘 {used / 1024.0 / 1024 / 1024:F0}/{c.TotalSize / 1024.0 / 1024 / 1024:F0}GB";
            }

            Pet.Say($"CPU {cpu:F0}% · 内存 {usedGb:F1}/{totalGb:F1}GB · {disk}", 5000);
        }
        catch
        {
            Pet.Say("系统信息读取失败", 3000);
        }
    }

    /// <summary>两次 GetSystemTimes 采样（间隔 300ms）计算 CPU 使用率</summary>
    private static async Task<double> GetCpuUsageAsync()
    {
        GetSystemTimes(out var i1, out var k1, out var u1);
        await Task.Delay(300);
        GetSystemTimes(out var i2, out var k2, out var u2);

        var idle = ToUlong(i2) - ToUlong(i1);
        var busy = (ToUlong(k2) + ToUlong(u2)) - (ToUlong(k1) + ToUlong(u1));
        var total = idle + busy;
        return total == 0 ? 0 : (1.0 - (double)idle / total) * 100.0;
    }

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;
}
