using DesktopPet.Sdk;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopPet.Core;

namespace DesktopPet.Builtin.TodoList;

/// <summary>待办清单窗口：添加（输入 + 回车/按钮）、勾选完成、删除。任何变化实时保存。</summary>
public sealed class TodoWindow : Window
{
    private static Brush Bg => NekoPalette.Brush(NekoDesign.Current.Background);
    private static Brush TextMain => NekoPalette.Brush(NekoDesign.Current.Text);
    private static Brush TextSub => NekoPalette.Brush(NekoDesign.Current.Muted);
    private static Brush CardBg => NekoPalette.Brush(NekoDesign.Current.Surface);
    private static Brush CardBorder => NekoPalette.Brush(NekoDesign.Current.Line);

    private readonly List<TodoItem> _items;
    private readonly Action<List<TodoItem>> _save;
    private readonly ISystem _sys;
    private readonly ListBox _listBox;
    private readonly TextBox _input;

    public TodoWindow(List<TodoItem> items, Action<List<TodoItem>> save, ISystem system)
    {
        _items = items;
        _save = save;
        _sys = system;
        Title = "Neko 待办清单";
        Width = 480;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MinWidth = 380;
        MinHeight = 240;
        Background = Bg;
        NekoDesign.Apply(this);
        Opacity = 0;

        var root = new DockPanel { Margin = new Thickness(24) };
        var header = NekoDesign.ToolHeader("一件一件来", "写下要做的事，完成一件就打个勾。");
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        _listBox = new ListBox
        {
            FontSize = 13,
            Foreground = TextMain,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
        };

        // 底部：删除选中 + 输入添加
        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        System.Windows.Automation.AutomationProperties.SetName(_input, "新的待办事项");
        _input.ToolTip = "输入待办事项，回车添加";
        del.Click += (_, _) => { if (_listBox.SelectedIndex >= 0 && _listBox.SelectedIndex < _items.Count) { _items.RemoveAt(_listBox.SelectedIndex); Refresh(); Save(); } };
        add.Click += (_, _) => AddInput();
        _input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddInput(); };
        Grid.SetColumn(_input, 1); Grid.SetColumn(add, 2);
        bottom.Children.Add(del);
        bottom.Children.Add(_input);
        bottom.Children.Add(add);
        DockPanel.SetDock(bottom, Dock.Bottom);

        Refresh();
        root.Children.Add(bottom);
        root.Children.Add(_listBox);
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
                Content = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap,
                    TextDecorations = item.Done ? TextDecorations.Strikethrough : null, Opacity = item.Done ? 0.55 : 1 },
                IsChecked = item.Done,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 2),
                Foreground = TextMain,
            };
            cb.Click += (_, _) => { item.Done = cb.IsChecked == true;
                if (cb.Content is TextBlock text) { text.TextDecorations = item.Done ? TextDecorations.Strikethrough : null; text.Opacity = item.Done ? 0.55 : 1; }
                Save(); };
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
        // v0.1.83：落盘失败是真故障（数据可能丢）——经插件日志通道上报（宿主会加 [插件:<id>] 前缀）；绝不外抛（铁律 7）
        try { _save(_items); } catch (Exception ex) { _sys.Log(PluginLogLevel.Warn, $"待办落盘失败：{ex.Message}"); }
    }
}
