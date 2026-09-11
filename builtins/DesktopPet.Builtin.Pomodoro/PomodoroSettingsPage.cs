using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Pomodoro;

/// <summary>
/// 番茄钟设置页：工作时长选择（5/15/25/45 分钟），持久化到 Storage（workMinutes）。
/// v0.1.63：改用 PluginUi 深色按钮；修复"storage 值不在候选项时无预选"的问题。
/// </summary>
public sealed class PomodoroSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private const string WorkKey = "workMinutes";
    private const int DefaultMinutes = 25;
    private static readonly int[] Presets = [5, 15, 25, 45];

    public PomodoroSettingsPage(IPluginStorage storage) => _storage = storage;

    public string Id => "com.lantern.pomodoro";
    public string Title => "com.lantern.pomodoro";

    public object BuildContent()
    {
        var current = _storage.Contains(WorkKey) ? _storage.Get<int>(WorkKey) : DefaultMinutes;

        var stack = new StackPanel { Margin = new Thickness(8) };

        var caption = PluginUi.Text("工作时长（分钟）", sub: true);
        caption.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(caption);

        // v0.1.63 fix：候选项 = 预设 ∪ {当前值}（当前值不在预设里也保证有预选）
        var options = Presets.Contains(current) ? Presets.ToArray() : Presets.Append(current).OrderBy(x => x).ToArray();

        var combo = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in options)
        {
            var item = new ComboBoxItem { Content = $"{c} 分钟" };
            if (c == current) item.IsSelected = true;
            combo.Items.Add(item);
        }

        var hint = PluginUi.Text("下次启动番茄钟时生效（径向菜单文案会同步）", sub: true);
        hint.FontSize = 11;
        hint.Margin = new Thickness(0, 8, 0, 0);

        var saveBtn = PluginUi.Button("保存");
        saveBtn.Margin = new Thickness(0, 8, 0, 0);
        saveBtn.Click += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem sel && sel.Content is string txt)
            {
                var minutes = int.Parse(txt.Split(' ')[0]);
                _storage.Set(WorkKey, minutes);
                MessageBox.Show($"已保存：{minutes} 分钟", "番茄钟设置", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };

        stack.Children.Add(combo);
        stack.Children.Add(hint);
        stack.Children.Add(saveBtn);
        return stack;
    }
}
