using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Core;

namespace DesktopPet.Builtin.Pomodoro;

/// <summary>
/// 番茄钟设置页：工作时长选择（5/15/25/45 分钟），持久化到 Storage（workMinutes）。
/// </summary>
public sealed class PomodoroSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private const string WorkKey = "workMinutes";
    private const int DefaultMinutes = 25;
    private static readonly int[] Choices = [5, 15, 25, 45];

    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public PomodoroSettingsPage(IPluginStorage storage) => _storage = storage;

    public string Id => "com.lantern.pomodoro";
    public string Title => "com.lantern.pomodoro";

    public object BuildContent()
    {
        var current = _storage.Contains(WorkKey) ? _storage.Get<int>(WorkKey) : DefaultMinutes;

        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(new TextBlock
        {
            Text = "工作时长（分钟）",
            FontSize = 12,
            Foreground = TextSub,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var combo = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in Choices)
        {
            var item = new ComboBoxItem { Content = $"{c} 分钟" };
            if (c == current) item.IsSelected = true;
            combo.Items.Add(item);
        }

        var hint = new TextBlock
        {
            Text = "下次启动番茄钟时生效（径向菜单文案会同步）",
            FontSize = 11,
            Foreground = TextSub,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var saveBtn = new Button { Content = "保存", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0) };
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
