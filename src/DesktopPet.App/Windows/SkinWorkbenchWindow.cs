using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopPet.App.Services;
using DesktopPet.Sdk;

namespace DesktopPet.App.Windows;

public sealed class SkinWorkbenchWindow : Window
{
    private string _source;
    private SkinReview _review;
    private SkinPreviewSurface _surface = null!;
    private readonly Border _preview = new() { Height = 300, Margin = new Thickness(0, 12, 0, 12) };
    private readonly ComboBox _states = new() { MinWidth = 170, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _play = PluginUi.Button("暂停");
    private readonly TextBlock _frame = PluginUi.Text("", sub: true), _details = PluginUi.Text("", sub: true),
        _status = PluginUi.Text("", sub: true), _report = PluginUi.Text("", sub: true);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private long _lastTick;
    private bool _guides = true;
    private double _speed = 1;

    public SkinWorkbenchWindow(string source)
    {
        _review = SkinReview.Open(source); _source = Path.GetFullPath(source);
        Title = "Neko 皮肤工作台"; Width = 760; Height = 790; MinWidth = 480; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NekoDesign.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) };
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var actions = new WrapPanel();
        Button ActionButton(string text, Action action)
        {
            var button = PluginUi.Button(text); button.Margin = new Thickness(0, 0, 10, 8);
            button.Click += (_, _) => action(); actions.Children.Add(button); return button;
        }
        ActionButton("选择其他目录", PickFolder);
        ActionButton("重新校验", () => Reload(_source));
        ActionButton("导出皮肤包", Export);
        _status.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(actions); footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(NekoDesign.ToolHeader("把每一个动作看清楚", "源素材预览 · 不改动正在使用的猫与原始文件"));
        _details.TextWrapping = TextWrapping.Wrap; _details.Margin = new Thickness(0, 12, 0, 12); content.Children.Add(_details);
        var controls = new WrapPanel();
        controls.Children.Add(_states); controls.Children.Add(_play);
        _play.Click += (_, _) => { _surface.Playing = !_surface.Playing; _lastTick = Environment.TickCount64; UpdateFrame(); };
        var previous = PluginUi.Button("上一帧"); previous.Margin = new Thickness(10, 0, 0, 0);
        var next = PluginUi.Button("下一帧"); next.Margin = new Thickness(10, 0, 0, 0);
        previous.Click += (_, _) => _surface.StepFrame(-1); next.Click += (_, _) => _surface.StepFrame(1);
        controls.Children.Add(previous); controls.Children.Add(next);
        content.Children.Add(controls);
        content.Children.Add(_preview);
        _frame.TextWrapping = TextWrapping.Wrap; content.Children.Add(_frame);
        var options = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        var guides = new CheckBox { Content = "画幅与眼层锚点", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        guides.Checked += (_, _) => SetGuides(true); guides.Unchecked += (_, _) => SetGuides(false);
        options.Children.Add(guides);
        var speed = new ComboBox { ItemsSource = new[] { "0.5× 慢放", "1× 原速", "2× 快放" }, SelectedIndex = 1, Width = 120, Margin = new Thickness(16, 0, 0, 0) };
        speed.SelectionChanged += (_, _) => { _speed = new[] { .5, 1, 2 }[speed.SelectedIndex]; _surface.Playback = _speed; };
        options.Children.Add(speed);
        content.Children.Add(options);
        var hint = PluginUi.Text("红框为源画幅，蓝色为眼层原点及可用包围盒。这里按清单循环身体帧与眼层，不模拟随机眨眼、眼神跟随、呼吸或自动过渡；附加过渡帧可在动作列表单独检查。", sub: true);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(hint);
        _report.TextWrapping = TextWrapping.Wrap; content.Children.Add(_report);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _states.SelectionChanged += (_, _) => { if (_states.SelectedItem is string name) { _surface.SelectState(name); UpdateFrame(); } };
        ApplyReview(_review);
        _timer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            _surface.Tick(TimeSpan.FromMilliseconds(Math.Clamp(now - _lastTick, 0L, 250L)));
            _lastTick = now;
        };
        IsVisibleChanged += (_, _) => UpdateTimer(); StateChanged += (_, _) => UpdateTimer();
        Closed += (_, _) => _timer.Stop();
    }

    private void SetGuides(bool enabled) { _guides = enabled; _surface.ShowGuides = enabled; _surface.InvalidateVisual(); }
    private void UpdateTimer()
    {
        if (IsVisible && WindowState != WindowState.Minimized && _surface.Playing && _surface.State.Fps > 0)
        {
            if (!_timer.IsEnabled) { _lastTick = Environment.TickCount64; _timer.Start(); }
        }
        else _timer.Stop();
    }
    private void ApplyReview(SkinReview review)
    {
        var selected = _states.SelectedItem as string;
        var playing = _surface?.Playing ?? true;
        if (_surface is not null) _surface.FrameChanged -= UpdateFrame;
        _review = review;
        _surface = new SkinPreviewSurface(review.Atlas) { ShowGuides = _guides, Playback = _speed, Playing = playing };
        _surface.FrameChanged += UpdateFrame; _preview.Child = _surface;
        _states.ItemsSource = review.Info.States.Select(s => s.Name).ToArray();
        _states.SelectedItem = selected is not null && review.Atlas.States.ContainsKey(selected) ? selected : "idle";
        _details.Text = $"{review.Info.DisplayName} · {review.Info.Id} · {review.Info.FrameWidth}×{review.Info.FrameHeight} · 每动作 {review.Info.Frames} 帧\n{_source}";
        _report.Text = $"基础校验通过：{review.Info.States.Count} 组动作，{review.Info.Transitions.Count} 条过渡声明。\n"
            + (review.Warnings.Count == 0 ? "未发现额外提示。仍需检查动作表现与素材许可。" : "作者提示（不阻止打包）：\n• " + string.Join("\n• ", review.Warnings));
        UpdateFrame();
    }
    private void UpdateFrame()
    {
        var state = _surface.State; var index = _surface.FrameIndex;
        var offset = state.EyeOffsets.Count > index ? state.EyeOffsets[index] : default;
        _frame.Text = $"{state.Name} · 帧 {index + 1}/{state.Frames.Length} · {state.Fps:0.##} fps · 眼层 {state.EyeLayer ?? "无"} · 锚点 ({offset.X:0.##}, {offset.Y:0.##}) 源像素";
        _play.Content = _surface.Playing ? "暂停" : "播放";
        UpdateTimer();
    }
    private void Reload(string source)
    {
        try
        {
            var review = SkinReview.Open(source); // 失败时保留原来已加载的预览。
            _source = Path.GetFullPath(source); ApplyReview(review);
            _status.Text = "已从磁盘重新校验并加载。";
        }
        catch (Exception ex) { _status.Text = "校验失败，保留上一份预览：" + ex.Message; }
    }
    private void PickFolder()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含 skin.json 的皮肤目录" };
        if (picker.ShowDialog(this) == true) Reload(picker.FolderName);
    }
    private void Export()
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "导出皮肤包（使用新文件名）", Filter = "皮肤包 (*.zip)|*.zip", FileName = _review.Info.Id + ".zip", OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            ExtensionInstaller.ExportSkin(_source, picker.FileName, _review.Info.Id);
            _status.Text = "已导出。接收者解压后选择包含 skin.json 的目录导入；原素材未修改。";
        }
        catch (Exception ex) { _status.Text = "导出未完成：" + ex.Message; }
    }
}
