using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopPet.App.Core;
using DesktopPet.App.Services;

namespace DesktopPet.App.Windows;

public sealed partial class SettingsWindow
{
    private void AddRecoveryCards(StackPanel stack)
    {
        stack.Children.Add(MakeSectionHeader("常驻与恢复"));
        stack.Children.Add(BuildToggleCard("安静模式", "暂停猫的气泡与托盘提醒，不补发错过的通知。", _config.QuietMode, _pet.SetQuietMode));
        stack.Children.Add(BuildToggleCard("电池省电", "使用电池时降低猫的动画与眼神刷新频率。", _config.BatterySaverEnabled, on => { _config.BatterySaverEnabled = on; Save(); }));
        stack.Children.Add(BuildButtonCard("找回猫咪", "将猫带回主屏幕，并重新对齐键盘。", "找回", _pet.FindCat));
        stack.Children.Add(BuildButtonCard("导出偏好设置", "包含外观、称呼和热键；不包含插件数据、凭据、开机自启和插件启用状态。", "导出", ExportPreferences));
        stack.Children.Add(BuildButtonCard("导入偏好设置", "预览后在下次正常启动时应用；自动保留导入前的原配置。", "导入", ImportPreferences));
        stack.Children.Add(BuildButtonCard("取消待导入设置", "取消尚未应用的导入，当前设置不变。", "取消待导入", () =>
        {
            try { File.Delete(SettingsBackup.PendingPath); MessageBox.Show(this, "已取消待导入设置。", "Neko"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "取消失败"); }
        }));
        stack.Children.Add(BuildButtonCard("恢复默认偏好", "下次正常启动恢复外观、互动与热键；保留插件数据、插件启用状态和开机自启。", "恢复默认", () =>
            PreviewPreferences(SettingsBackup.Export(new AppConfig { CatName = "小橘", OwnerTitle = "你", UpdateCheckEnabled = false }))));
        stack.Children.Add(BuildButtonCard(_config.SafeMode ? "当前为安全模式" : "安全启动", "安全模式暂不加载任何插件，并使用默认皮肤；原来的插件开关和皮肤选择会保留。", _config.SafeMode ? "恢复正常启动" : "重启到安全模式", RestartForRecovery));
    }

    private void RestartForRecovery()
    {
        if (MessageBox.Show(this, _config.SafeMode ? "关闭当前窗口并恢复正常启动？" : "关闭当前窗口并进入安全模式排查问题？", "重启 Neko", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            var exe = Environment.ProcessPath ?? throw new IOException("找不到程序路径。");
            if (!string.Equals(Path.GetFileName(exe), "Neko.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请从正式 Neko 程序使用重启功能。");
            var arguments = (_config.SafeMode ? "" : "--safe-mode ") + "--wait-for-process=" + Environment.ProcessId;
            if (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Arguments = arguments }) is null)
                throw new IOException("新进程未启动，当前程序继续运行。");
            Application.Current.Shutdown();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "重启未完成"); }
    }

    private void ExportPreferences()
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "导出 Neko 偏好设置", FileName = "neko-settings.json", Filter = "JSON 设置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, SettingsBackup.Export(_config)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败"); }
    }

    private void ImportPreferences()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "导入 Neko 偏好设置", Filter = "JSON 设置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try { PreviewPreferences(SettingsBackup.Read(picker.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导入未完成"); }
    }

    private void PreviewPreferences(string json)
    {
        try
        {
            var next = SettingsBackup.Preview(json, _config);
            var details = $"猫咪：{next.CatName}\n称呼：{next.OwnerTitle}\n皮肤：{next.Skin}\n猫咪倍率：{next.Scale:F1}\n键盘倍率：{next.KeyboardScale:F1}\n主题：{next.Theme}\n安静模式：{next.QuietMode}\n自动检查更新：{next.UpdateCheckEnabled}\n\n此外会应用文件中的互动与热键偏好。下次正常启动时生效，应用前自动备份原配置；可在本页取消。";
            if (new ExtensionPreviewWindow("导入偏好设置", details, null, "下次启动应用") { Owner = this }.ShowDialog() != true) return;
            SettingsBackup.Stage(json, _config, SettingsBackup.PendingPath);
            MessageBox.Show(this, "已保存待导入设置，下次正常启动时应用。", "Neko");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导入未完成"); }
    }
}
