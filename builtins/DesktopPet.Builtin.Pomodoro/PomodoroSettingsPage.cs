using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Pomodoro;

public sealed class PomodoroSettingsPage(IPluginStorage storage, Action? changed = null) : ISettingsPage
{
    public string Id => "com.lantern.pomodoro";
    public string Title => "com.lantern.pomodoro";
    public object BuildContent()
    {
        var stack = new StackPanel { Margin = new Thickness(8) };
        ComboBox Duration(string label, string key, int fallback, int max, int[] presets)
        {
            stack.Children.Add(PluginUi.Text(label, sub: true));
            var current = Math.Clamp(storage.Contains(key) ? storage.Get<int>(key) : fallback, 1, max);
            var combo = new ComboBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 16) };
            foreach (var value in presets.Append(current).Distinct().Order())
                combo.Items.Add(new ComboBoxItem { Content = $"{value} 分钟", Tag = value, IsSelected = value == current });
            stack.Children.Add(combo);
            return combo;
        }
        var work = Duration("专注时长", "workMinutes", 25, 120, [5, 15, 25, 45]);
        var rest = Duration("休息时长", "restMinutes", 5, 30, [1, 3, 5, 10, 15]);
        stack.Children.Add(PluginUi.Text("保存后从下一阶段开始生效；不会改变正在运行的计时。", sub: true));
        var save = PluginUi.Button("保存");
        save.Margin = new Thickness(0, 12, 0, 0);
        save.Click += (_, _) =>
        {
            try
            {
                storage.Set("workMinutes", (int)((ComboBoxItem)work.SelectedItem).Tag);
                storage.Set("restMinutes", (int)((ComboBoxItem)rest.SelectedItem).Tag);
                changed?.Invoke();
                PluginUi.ShowStatus(stack, "已保存");
            }
            catch { PluginUi.ShowStatus(stack, "保存失败，请重试。"); }
        };
        stack.Children.Add(save);
        return stack;
    }
}
