using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopPet.Builtin.ClipboardHistory;

/// <summary>剪贴板历史窗口：ListBox 展示最近条目，双击复制回剪贴板。</summary>
public sealed class ClipboardWindow : Window
{
    public ClipboardWindow(List<string> history, Action<string> pick)
    {
        Title = "Neko 剪贴板历史";
        Width = 380;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(8) };
        var hint = new TextBlock
        {
            Text = "双击条目复制回剪贴板",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        };
        DockPanel.SetDock(hint, Dock.Top);

        var listBox = new ListBox { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        if (history.Count == 0)
        {
            listBox.Items.Add(new ListBoxItem { Content = "（暂无历史，复制点文本会自动记录）", IsEnabled = false });
        }
        else
        {
            foreach (var item in history)
            {
                listBox.Items.Add(new ListBoxItem { Content = Truncate(item, 50), Tag = item, ToolTip = item });
            }
        }
        listBox.MouseDoubleClick += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem { Tag: string s } && !string.IsNullOrEmpty(s))
            {
                try { pick(s); } catch { }
                Close();
            }
        };

        root.Children.Add(hint);
        root.Children.Add(listBox);
        Content = root;
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
