using System.Windows;
using System.Windows.Controls;

namespace DesktopPet.Builtin.TodoList;

/// <summary>待办清单窗口：添加（输入 + 回车/按钮）、勾选完成、删除。任何变化实时保存。</summary>
public sealed class TodoWindow : Window
{
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

        var root = new DockPanel { Margin = new Thickness(10) };

        // 底部：删除选中 + 输入添加
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var del = new Button { Content = "删除选中", Padding = new Thickness(10, 4, 10, 4) };
        _input = new TextBox { FontSize = 13, Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var add = new Button { Content = "添加", Padding = new Thickness(12, 4, 12, 4) };
        del.Click += (_, _) => { if (_listBox.SelectedIndex >= 0 && _listBox.SelectedIndex < _items.Count) { _items.RemoveAt(_listBox.SelectedIndex); Refresh(); Save(); } };
        add.Click += (_, _) => AddInput();
        _input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddInput(); };
        bottom.Children.Add(del);
        bottom.Children.Add(_input);
        bottom.Children.Add(add);
        DockPanel.SetDock(bottom, Dock.Bottom);

        _listBox = new ListBox { FontSize = 13 };
        Refresh();
        DockPanel.SetDock(_listBox, Dock.Top);

        root.Children.Add(_listBox);
        root.Children.Add(bottom);
        Content = root;
    }

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
            };
            cb.Click += (_, _) => { item.Done = cb.IsChecked == true; Save(); };
            _listBox.Items.Add(cb);
        }
        if (_items.Count == 0)
        {
            _listBox.Items.Add(new ListBoxItem { Content = "（空，输入下方文本添加）", IsEnabled = false });
        }
    }

    private void Save()
    {
        try { _save(_items); } catch { }
    }
}
