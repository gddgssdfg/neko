using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.ClipboardHistory;

/// <summary>Searchable clipboard library. Selection alone never writes to the clipboard.</summary>
public sealed class ClipboardWindow : Window
{
    private Action? _refresh;
    public void RefreshHistory() => _refresh?.Invoke();

    public ClipboardWindow(List<string> history, Action<string> pick, ISystem system, FrameworkElement? controls = null, Func<List<string>>? loadHistory = null, Func<bool>? recordingState = null)
    {
        Title = "Neko 剪贴板历史";
        Width = 480; Height = 540; MinWidth = 360; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        NekoDesign.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = NekoDesign.ToolHeader("刚刚复制过", "搜索历史，双击或按回车复制选中内容。");
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        if (controls is not null) { controls.Margin = new Thickness(0,0,0,16); DockPanel.SetDock(controls,Dock.Top); root.Children.Add(controls); }
        var search = PluginUi.Input();
        search.ToolTip = "搜索剪贴板内容";
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索剪贴板");
        search.Margin = new Thickness(0, 0, 0, 14);
        DockPanel.SetDock(search, Dock.Top); root.Children.Add(search);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var copy = PluginUi.Button("复制选中");
        DockPanel.SetDock(copy, Dock.Right); footer.Children.Add(copy);
        var status = PluginUi.Text("", sub: true); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var list = new ListBox();
        root.Children.Add(list); Content = root;
        void Refresh()
        {
            if (loadHistory is not null) history = loadHistory();
            list.Items.Clear();
            var matches = history.Where(text => text.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var text in matches)
                list.Items.Add(new ListBoxItem { Tag = text, ToolTip = text,
                    Content = new TextBlock { Text = text.Replace('\r', ' ').Replace('\n', ' '),
                        TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44, TextWrapping = TextWrapping.Wrap } });
            status.Text = (recordingState?.Invoke() == false ? "记录已暂停 · " : "") + $"{matches.Count} 条记录";
            if (matches.Count == 0)
                list.Items.Add(new ListBoxItem { IsEnabled = false, Content = history.Count == 0 ? recordingState?.Invoke() == false ? "记录已暂停。请先开启记录，再复制文字。" : "还没有记录，复制一段文字试试。" : "没有匹配的内容。" });
            copy.IsEnabled = false;
        }
        void CopySelection()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: string text }) return;
            try { pick(text); Close(); }
            catch (Exception ex) { status.Text = "复制失败，请重试"; system.Log(PluginLogLevel.Warn, $"写回剪贴板失败：{ex.Message}"); }
        }
        search.TextChanged += (_, _) => Refresh();
        list.SelectionChanged += (_, _) => copy.IsEnabled = list.SelectedItem is ListBoxItem { Tag: string };
        list.MouseDoubleClick += (_, _) => CopySelection();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CopySelection(); e.Handled = true; } };
        copy.Click += (_, _) => CopySelection();
        _refresh = Refresh;
        Activated += (_, _) => Refresh();
        Closed += (_, _) => _refresh = null;
        Refresh();
        Loaded += (_, _) => search.Focus();
    }
}
