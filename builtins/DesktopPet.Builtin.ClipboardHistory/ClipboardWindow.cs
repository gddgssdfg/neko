using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPet.Builtin.ClipboardHistory;

/// <summary>剪贴板历史窗口：ListBox 展示最近条目，双击复制回剪贴板。</summary>
public sealed class ClipboardWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x2F));
    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
    private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x2B, 0x36, 0x47));
    private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x47, 0x55));
    private static readonly Brush SelBg = new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x5C));

    public ClipboardWindow(List<string> history, Action<string> pick)
    {
        Title = "Neko 剪贴板历史";
        Width = 380;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Opacity = 0;

        var root = new DockPanel { Margin = new Thickness(8) };
        var hint = new TextBlock
        {
            Text = "双击条目复制回剪贴板",
            FontSize = 11,
            Foreground = TextSub,
        };
        DockPanel.SetDock(hint, Dock.Top);

        var listBox = new ListBox
        {
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
        };
        listBox.ItemContainerStyle = MakeItemStyle();
        if (history.Count == 0)
        {
            listBox.Items.Add(new ListBoxItem { Content = "（暂无历史，复制点文本会自动记录）", IsEnabled = false });
        }
        else
        {
            foreach (var item in history)
            {
                listBox.Items.Add(new ListBoxItem
                {
                    Content = Truncate(item, 50),
                    Tag = item,
                    ToolTip = item,
                    Foreground = TextMain,
                });
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

        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static Style MakeItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(ForegroundProperty, TextMain));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 3, 6, 3)));
        var trigger = new Trigger
        {
            Property = ListBoxItem.IsSelectedProperty,
            Value = true,
        };
        trigger.Setters.Add(new Setter(BackgroundProperty, SelBg));
        trigger.Setters.Add(new Setter(ForegroundProperty, TextMain));
        style.Triggers.Add(trigger);
        return style;
    }
}
