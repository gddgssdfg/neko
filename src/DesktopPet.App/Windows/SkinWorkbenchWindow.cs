using System.IO;
using System.Globalization;
using DesktopPet.App.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopPet.App.Services;
using DesktopPet.App.Renderer;
using DesktopPet.Sdk;

namespace DesktopPet.App.Windows;

public sealed class SkinWorkbenchWindow : Window
{
    private string _source;
    private SkinReview _review;
    private SkinAnchorEditor _editor;
    private readonly TextBox _anchorX = new() { Width = 72, Margin = new Thickness(6, 0, 12, 0) },
        _anchorY = new() { Width = 72, Margin = new Thickness(6, 0, 12, 0) };
    private readonly CheckBox _gazePreview = new() { Content = "模拟视线（不写入皮肤）" },
        _unclippedGaze = new() { Content = "对比裁剪前眼层（半透明）", Margin = new Thickness(0, 10, 0, 0) };
    private readonly Expander _gazePanel = new() { Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _gazeInfo = PluginUi.Text("", sub: true);
    private readonly List<Button> _gazeButtons = [];
    private Vector _gazeDirection;
    private readonly CheckBox _editAnchors = new() { Content = "编辑眼层锚点（拖动蓝框）" };
    private readonly Button _applyAnchor = PluginUi.Button("应用坐标"), _undo = PluginUi.Button("撤销"),
        _redo = PluginUi.Button("重做"), _restore = PluginUi.Button("还原本帧");
    private Button _save = null!, _export = null!;
    private SkinPreviewSurface _surface = null!;
    private readonly Grid _previewLayout = new();
    private readonly Border _previewBody = new();
    private readonly System.Windows.Controls.Primitives.UniformGrid _gazeDirections = new()
        { Rows = 3, Columns = 3, Width = 144, Height = 120, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
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
        _editor = new SkinAnchorEditor(source); _review = _editor.Review; _source = Path.GetFullPath(source);
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
        _save = ActionButton("保存锚点", () => SaveEdits());
        _export = ActionButton("导出皮肤包", Export);
        _status.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(actions); footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        content.Children.Add(NekoDesign.ToolHeader("把每一个动作看清楚", "源素材预览 · 修改先留在草稿，保存后写回清单"));
        _details.TextWrapping = TextWrapping.Wrap; _details.Margin = new Thickness(0, 12, 0, 12); content.Children.Add(_details);
        var controls = new WrapPanel();
        controls.Children.Add(_states); controls.Children.Add(_play);
        _play.Click += (_, _) => { _surface.Playing = !_surface.Playing; _lastTick = Environment.TickCount64; UpdateFrame(); };
        var previous = PluginUi.Button("上一帧"); previous.Margin = new Thickness(10, 0, 0, 0);
        var next = PluginUi.Button("下一帧"); next.Margin = new Thickness(10, 0, 0, 0);
        previous.Click += (_, _) => _surface.StepFrame(-1); next.Click += (_, _) => _surface.StepFrame(1);
        controls.Children.Add(previous); controls.Children.Add(next);
        content.Children.Add(controls);
        _previewLayout.ColumnDefinitions.Add(new ColumnDefinition());
        _previewLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _previewLayout.Children.Add(_previewBody);
        Grid.SetColumn(_gazeDirections, 1); _previewLayout.Children.Add(_gazeDirections);
        _preview.Child = _previewLayout;
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
        _gazePanel.Header = PluginUi.Text("眼神位移与遮罩检查");
        var gazeContent = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        gazeContent.Children.Add(_gazePreview); gazeContent.Children.Add(_unclippedGaze);
        var labels = new[] { "↖", "↑", "↗", "←", "居中", "→", "↙", "↓", "↘" };
        for (int i = 0; i < labels.Length; i++)
        {
            var direction = new Vector(i % 3 - 1, i / 3 - 1);
            var button = PluginUi.Button(labels[i]); button.Tag = direction;
            button.Padding = new Thickness(0, 4, 0, 4);
            button.Margin = new Thickness(0, 0, 4, 4);
            button.ToolTip = $"视线方向 ({direction.X}, {direction.Y})";
            button.Click += (_, _) => { _gazeDirection = direction; _surface.GazeDirection = direction; _surface.InvalidateVisual(); UpdateFrame(); };
            _gazeButtons.Add(button); _gazeDirections.Children.Add(button);
        }
        _gazeInfo.TextWrapping = TextWrapping.Wrap; gazeContent.Children.Add(_gazeInfo);
        _gazePanel.Content = gazeContent; content.Children.Add(_gazePanel);
        _gazePreview.Checked += (_, _) => { _surface.CancelAnchorDrag(); _editAnchors.IsChecked = false; _surface.Playing = false; UpdateFrame(); };
        _gazePreview.Unchecked += (_, _) => UpdateFrame();
        _unclippedGaze.Checked += (_, _) => UpdateFrame(); _unclippedGaze.Unchecked += (_, _) => UpdateFrame();
        var editing = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        editing.Children.Add(_editAnchors);
        var coordinates = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        coordinates.Children.Add(PluginUi.Text("X")); coordinates.Children.Add(_anchorX);
        coordinates.Children.Add(PluginUi.Text("Y")); coordinates.Children.Add(_anchorY); coordinates.Children.Add(_applyAnchor);
        editing.Children.Add(coordinates);
        var history = new WrapPanel();
        foreach (var button in new[] { _undo, _redo, _restore }) { button.Margin = new Thickness(0, 0, 10, 0); history.Children.Add(button); }
        editing.Children.Add(history); content.Children.Add(editing);
        _editAnchors.Checked += (_, _) => { _surface.Playing = false; UpdateFrame(); };
        _editAnchors.Unchecked += (_, _) => { _surface.CancelAnchorDrag(); UpdateFrame(); };
        _applyAnchor.Click += (_, _) =>
        {
            if (!double.TryParse(_anchorX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(_anchorY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            { _status.Text = "坐标须为数字，小数使用英文句点。"; return; }
            ApplyAnchor(new SkinEyeOffset(x, y));
        };
        _undo.Click += (_, _) => RevealEdit(_editor.Undo());
        _redo.Click += (_, _) => RevealEdit(_editor.Redo());
        _restore.Click += (_, _) => { _editor.RestoreFrame(_surface.State.Name, _surface.FrameIndex); RefreshDraft(); };
        var hint = PluginUi.Text("红框为源画幅，蓝色为眼层原点及可用包围盒。这里按清单循环身体帧与眼层；视线可在检查面板模拟，不叠加随机眨眼、呼吸或自动过渡；附加过渡帧可在动作列表单独检查。", sub: true);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(hint);
        _report.TextWrapping = TextWrapping.Wrap; content.Children.Add(_report);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        _states.SelectionChanged += (_, _) => { if (_states.SelectedItem is string name) { _surface.SelectState(name); UpdateFrame(); } };
        ApplyReview(_editor);
        _timer.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            _surface.Tick(TimeSpan.FromMilliseconds(Math.Clamp(now - _lastTick, 0L, 250L)));
            _lastTick = now;
        };
        IsVisibleChanged += (_, _) => UpdateTimer(); StateChanged += (_, _) => UpdateTimer();
        Closing += (_, e) => { if (!ConfirmLeave()) e.Cancel = true; };
        Closed += (_, _) => { _surface.CancelAnchorDrag(); _timer.Stop(); };
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
    private void ApplyReview(SkinAnchorEditor editor)
    {
        var review = editor.Review;
        var selected = _states.SelectedItem as string;
        var playing = _surface?.Playing ?? true;
        if (_surface is not null) { _surface.CancelAnchorDrag(); _surface.FrameChanged -= UpdateFrame; _surface.AnchorDragged -= ApplyAnchor; }
        _editor = editor; _review = review;
        _surface = new SkinPreviewSurface(review.Atlas) { ShowGuides = _guides, Playback = _speed, Playing = playing, OffsetProvider = editor.GetOffset };
        _surface.FrameChanged += UpdateFrame; _surface.AnchorDragged += ApplyAnchor; _previewBody.Child = _surface;
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
        var offset = _surface.CurrentEyeOffset;
        _frame.Text = $"{state.Name} · 帧 {index + 1}/{state.Frames.Length} · {state.Fps:0.##} fps · 眼层 {state.EyeLayer ?? "无"} · 锚点 ({offset.X:0.##}, {offset.Y:0.##}) 源像素";
        _play.Content = _surface.Playing ? "暂停" : "播放";
        var inspecting = _gazePreview.IsChecked == true;
        _surface.GazePreviewEnabled = inspecting;
        _surface.GazeDirection = _gazeDirection;
        _surface.ShowUnclippedGaze = _unclippedGaze.IsChecked == true;
        _editAnchors.IsEnabled = !inspecting;
        var editable = !inspecting && _editor.CanEdit(state.Name) && _editAnchors.IsChecked == true;
        foreach (var button in _gazeButtons) button.IsEnabled = inspecting && _surface.CanPreviewGaze;
        _unclippedGaze.IsEnabled = inspecting && _surface.HasSocketMask;
        _gazeDirections.Visibility = inspecting ? Visibility.Visible : Visibility.Collapsed;
        if (inspecting && _surface.CanPreviewGaze)
        {
            var gaze = _surface.GazeOffset;
            _frame.Text += $"\n视线方向 ({_gazeDirection.X}, {_gazeDirection.Y}) · 位移 ({gaze.X:0.###}, {gaze.Y:0.###}) 源像素 · " + (EyeGazeMath.IsFixedExpression(state.Name, state.EyeLayer!) ? "表情固定，不平移"
                : _surface.HasSocketMask ? "遮罩裁剪已启用" : "无闭合层遮罩，仅限幅，需目视检查");
        }
        UpdateGazeInfo();
        _surface.InvalidateVisual();
        _surface.AnchorEditingEnabled = editable;
        _anchorX.Text = offset.X.ToString("0.###", CultureInfo.InvariantCulture);
        _anchorY.Text = offset.Y.ToString("0.###", CultureInfo.InvariantCulture);
        _anchorX.IsEnabled = _anchorY.IsEnabled = _applyAnchor.IsEnabled = editable && !_surface.Playing && !_surface.IsAnchorDragging;
        _restore.IsEnabled = editable && !_surface.IsAnchorDragging;
        _undo.IsEnabled = _editor.CanUndo && !_surface.IsAnchorDragging;
        _redo.IsEnabled = _editor.CanRedo && !_surface.IsAnchorDragging;
        _save.IsEnabled = _editor.IsDirty && !_surface.IsAnchorDragging;
        _export.IsEnabled = !_editor.IsDirty && !_surface.IsAnchorDragging;
        _states.IsEnabled = _play.IsEnabled = !_surface.IsAnchorDragging;
        Title = "Neko 皮肤工作台" + (_editor.IsDirty ? " · 未保存" : "");
        UpdateTimer();
    }
    private void UpdateGazeInfo()
    {
        if (!_surface.GazePreviewEnabled) { _gazeInfo.Text = "勾选后，用九个方向检查静态极限视线；不会修改锚点或素材。"; return; }
        if (!_surface.CanPreviewGaze) { _gazeInfo.Text = "当前动作没有可用的独立眼层，或眼层尺寸与身体帧不一致，无法模拟。"; return; }
        var limits = _surface.GazeLimits; var offset = _surface.GazeOffset;
        var fixedExpression = EyeGazeMath.IsFixedExpression(_surface.State.Name, _surface.State.EyeLayer!);
        _gazeInfo.Text = $"方向 ({_gazeDirection.X}, {_gazeDirection.Y}) · 实际位移 ({offset.X:0.###}, {offset.Y:0.###}) 源像素\n"
            + $"橙色椭圆：中心可移动范围，X ±{limits.X:0.###} / Y ±{limits.Y:0.###} 源像素。\n"
            + (fixedExpression ? "当前为闭眼、半闭或抚摸表情，主程序固定眼层，不随视线移动。\n" : "")
            + (fixedExpression ? "当前表情不进行视线平移。" : _surface.HasSocketMask ? "绿色区域：主程序使用的闭合层 alpha 遮罩。开启对比可查看裁剪前眼层。"
                : "缺少可用的闭合层遮罩：只有椭圆限幅，无法自动确认眼眶安全，请逐帧目视检查。")
            + (_surface.GazeOutsideFrame ? "\n提示：未裁剪眼层的包围盒已超出源画幅，请检查锚点。" : "")
            + "\n遮罩会随锚点一起移动，不能据此保证眼睛与脸部对齐。预览不叠加打字、眨眼或行走镜像。";
    }
    private void RevealEdit((string State, int Frame)? location)
    {
        if (location is { } point) { _states.SelectedItem = point.State; _surface.SelectFrame(point.Frame); }
        RefreshDraft();
    }
    private void ApplyAnchor(SkinEyeOffset offset)
    {
        try { _surface.Playing = false; _editor.SetOffset(_surface.State.Name, _surface.FrameIndex, offset); RefreshDraft(); }
        catch (Exception ex) { _status.Text = "未修改锚点：" + ex.Message; UpdateFrame(); }
    }
    private void RefreshDraft()
    {
        _surface.Playing = false;
        _surface.InvalidateVisual(); UpdateFrame();
        _status.Text = _editor.IsDirty ? "有未保存的锚点修改；保存前会备份原清单。" : "锚点与已保存内容一致。";
    }
    private bool SaveEdits()
    {
        try
        {
            var backup = _editor.Save(Path.Combine(Paths.Data, "host", "skin-backups"));
            RefreshDraft();
            _status.Text = backup is null ? "没有需要保存的修改。" : "已保存锚点。原清单备份：" + backup + "。使用中的猫可在外观页刷新皮肤。";
            return true;
        }
        catch (Exception ex) { _status.Text = "保存未完成，草稿仍保留：" + ex.Message; return false; }
    }
    private bool ConfirmLeave()
    {
        _surface.CancelAnchorDrag();
        if (!_editor.IsDirty) return true;
        var choice = MessageBox.Show(this, "存在未保存的锚点修改，是否保存？\n是：保存后继续；否：放弃草稿；取消：留在工作台。", "锚点草稿", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice == MessageBoxResult.No || choice == MessageBoxResult.Yes && SaveEdits();
    }
    private void Reload(string source)
    {
        try
        {
            if (!ConfirmLeave()) return;
            var editor = new SkinAnchorEditor(source); // 失败时保留原来已加载的预览和草稿。
            _source = Path.GetFullPath(source); ApplyReview(editor);
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
        if (_editor.IsDirty) { _status.Text = "请先保存锚点，再导出包含修改的皮肤包。"; return; }
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
