using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPet.Builtin.TodoList;

/// <summary>待办清单窗口：添加（输入 + 回车/按钮）、勾选完成、删除。任何变化实时保存。</summary>
public sealed class TodoWindow : Window
{
    /// <summary>v0.1.82：冻结画刷 —— 冻结后可跨线程共享、省渲染开销；未冻结的 Freezable 在跨线程或高频渲染时是纯损耗。</summary>
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Bg = Frozen(0x1E, 0x26, 0x2F);
    private static readonly Brush TextMain = Frozen(0xE5, 0xE7, 0xEB);
    private static readonly Brush TextSub = Frozen(0x9C, 0xA3, 0xAF);
    private static readonly Brush CardBg = Frozen(0x2B, 0x36, 0x47);
    private static readonly Brush CardBorder = Frozen(0x3A, 0x47, 0x55);

    private readonly List<TodoItem> _items;
    private readonly Action<List<TodoItem>> _save;
    private readonly ListBox _listBox;
    private readonly TextBox _input;

    public TodoWindow(List<TodoItem> items, Action<List<TodoItem>> save)
    {
        _items = items;
        _save = save;
        Title = "Neko 待办清单";
        Width = 380;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 280;
        MinHeight = 240;
        Background = Bg;
        Opacity = 0;

        var root = new DockPanel { Margin = new Thickness(10) };

        _listBox = new ListBox
        {
            FontSize = 13,
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
        };

        // 底部：删除选中 + 输入添加
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var del = MakeButton("删除选中", new Thickness(10, 4, 10, 4));
        _input = new TextBox
        {
            FontSize = 13,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CaretBrush = TextMain,
        };
        var add = MakeButton("添加", new Thickness(12, 4, 12, 4));
        del.Click += (_, _) => { if (_listBox.SelectedIndex >= 0 && _listBox.SelectedIndex < _items.Count) { _items.RemoveAt(_listBox.SelectedIndex); Refresh(); Save(); } };
        add.Click += (_, _) => AddInput();
        _input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddInput(); };
        bottom.Children.Add(del);
        bottom.Children.Add(_input);
        bottom.Children.Add(add);
        DockPanel.SetDock(bottom, Dock.Bottom);

        Refresh();
        DockPanel.SetDock(_listBox, Dock.Top);

        root.Children.Add(_listBox);
        root.Children.Add(bottom);
        Content = root;

        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private static Button MakeButton(string text, Thickness padding) => new Button
    {
        Content = text,
        Padding = padding,
        Foreground = TextMain,
        Background = CardBg,
        BorderBrush = CardBorder,
        BorderThickness = new Thickness(1),
    };

    private void AddInput()
    {
        var text = _input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _items.Add(new TodoItem { Text = text });
        _input.Clear();
        Refresh();
        Save();
    }

    private void Refresh()
    {
        _listBox.Items.Clear();
        foreach (var item in _items)
        {
            var cb = new CheckBox
            {
                Content = item.Text,
                IsChecked = item.Done,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 2),
                Foreground = TextMain,
            };
            cb.Click += (_, _) => { item.Done = cb.IsChecked == true; Save(); };
            _listBox.Items.Add(cb);
        }
        if (_items.Count == 0)
        {
            var empty = new ListBoxItem { Content = "（空，输入下方文本添加）", IsEnabled = false };
            empty.Foreground = TextSub;
            _listBox.Items.Add(empty);
        }
    }

    private void Save()
    {
        try { _save(_items); } catch { }
    }
}
