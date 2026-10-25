using DesktopPet.Sdk;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopPet.App.Core;

namespace DesktopPet.App.Windows;

/// <summary>
/// 首次启动：默认名字、可跳过的称呼与功能选择。
/// </summary>
public sealed class FirstRunWindow : Window
{
    // 主题色（与 SettingsWindow 保持一致——保持自家身份，深色 + 蓝 accent）
    private static Color BgRoot => NekoDesign.Current.Background;
    private static Color BgCard => NekoDesign.Current.Surface;
    private static Color BgInput => NekoDesign.Current.Surface;
    private static Color BorderC => NekoDesign.Current.Line;
    private static Color Accent => NekoDesign.Current.Accent;
    private static Color TextMain => NekoDesign.Current.Text;
    private static Color TextSub => NekoDesign.Current.Muted;
    private static readonly Color TextErr = Color.FromRgb(0xE7, 0x4C, 0x3C);

    private readonly AppConfig _config;
    private readonly TextBox _txtName = MakeInput("奶糖/小橘/糖饼…");
    private readonly TextBox _txtTitle = MakeInput("主人/小可爱/老板…");
    private readonly TextBlock _lblErr;
    private readonly CheckBox _keyboard = new() { Content = "显示键盘和实时鼠标", IsChecked = true };
    private readonly CheckBox _network = new() { Content = "启动时自动检查更新（访问 GitHub，失败时尝试 Gitee）", IsChecked = false };
    private readonly CheckBox _clipboard = new() { Content = "记录剪贴板历史（可能包含敏感文本，仅本地保存）", IsChecked = false };

    public FirstRunWindow(AppConfig config)
    {
        _config = config;
        Title = "欢迎养 Neko";
        Width = 500;
        Height = Math.Min(720, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        NekoDesign.Apply(this, config.Theme == "light");
        Background = new SolidColorBrush(BgRoot);
        ShowInTaskbar = true;        // 引导期间显示在任务栏（首次必须找到窗口）
        Topmost = true;              // 置顶（首启别被猫挡住——猫其实还没出现）

        var root = new Grid { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var stack = new StackPanel();
        root.Children.Add(stack);

        // 顶部 accent 条（像素风身份标识）
        var accent = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Accent),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 22),
        };
        stack.Children.Add(accent);

        // 标题
        stack.Children.Add(new TextBlock
        {
            Text = "你好，我是你的桌面小伙伴。",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(TextMain),
            Margin = new Thickness(0, 0, 0, 8),
        });
        // 副标题
        stack.Children.Add(new TextBlock
        {
            Text = "先互相认识一下，再一起开始今天的生活。",
            FontSize = 13,
            Foreground = new SolidColorBrush(TextSub),
            Margin = new Thickness(0, 0, 0, 24),
        });

        // ---- 输入区 1：猫名 ----
        stack.Children.Add(MakeFieldLabel("给我取个名字"));
        var nameField = WrapField(_txtName);
        stack.Children.Add(nameField);
        stack.Children.Add(MakeFieldHint("气泡对话、宠物升级都会叫这个名字（≤12 字）"));

        // ---- 输入区 2：称呼 ----
        stack.Children.Add(MakeFieldLabel("我怎么称呼你"));
        stack.Children.Add(WrapField(_txtTitle));
        stack.Children.Add(MakeFieldHint("出现在休息提醒、闲聊等场景（≤12 字）"));

        stack.Children.Add(MakeFieldLabel("按你的习惯开始"));
        foreach (var option in new[] { _keyboard, _network, _clipboard })
        {
            var text = option.Content.ToString()!;
            System.Windows.Automation.AutomationProperties.SetName(option, text);
            option.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 365 };
            option.Margin = new Thickness(0, 7, 0, 7); option.Foreground = new SolidColorBrush(TextMain); stack.Children.Add(option);
        }
        stack.Children.Add(MakeFieldHint("天气和 AI 仅在主动查询时联网；这些选项以后都可在设置里修改。"));

        // 错误提示
        _lblErr = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(TextErr),
            Margin = new Thickness(0, 4, 0, 0),
        };
        stack.Children.Add(_lblErr);

        // ---- 按钮区 ----
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var btnCancel = MakeButton("退出", isPrimary: false);
        btnCancel.Margin = new Thickness(0, 0, 10, 0);
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var skip = MakeButton("使用默认设置", isPrimary: false);
        skip.Width = 130;
        skip.Margin = new Thickness(0,0,10,0);
        skip.Click += (_, _) => { _txtName.Text = "小橘"; _txtTitle.Text = "你"; _keyboard.IsChecked = true; _network.IsChecked = false; _clipboard.IsChecked = false; OnConfirm(); };
        btnRow.Children.Add(skip);
        var btnOk = MakeButton("开始", isPrimary: true);
        btnOk.Click += (_, _) => OnConfirm();
        btnOk.IsDefault = true;       // 回车触发
        btnRow.Children.Add(btnCancel);
        btnRow.Children.Add(btnOk);
        stack.Children.Add(btnRow);

        // 入场动画
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        // 拖动（无标题栏要给个能拖的手感）
        System.Windows.Automation.AutomationProperties.SetName(_txtName, "猫咪的名字");
        System.Windows.Automation.AutomationProperties.SetName(_txtTitle, "对你的称呼");
        Loaded += (_, _) => _txtName.Focus();

        // 引导窗口按 Esc 视为取消（退出程序）
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };

        // 预填（如果有旧值，比如上版本升级时已经填过但字段是空）
        _txtName.Text = string.IsNullOrWhiteSpace(_config.CatName) ? "小橘" : _config.CatName;
        _txtTitle.Text = string.IsNullOrWhiteSpace(_config.OwnerTitle) ? "你" : _config.OwnerTitle;
    }

    private void OnConfirm()
    {
        var name = _txtName.Text?.Trim() ?? "";
        var title = _txtTitle.Text?.Trim() ?? "";
        if (name.Length == 0) name = "小橘";
        if (title.Length == 0) title = "你";
        if (name.Length > 12 || title.Length > 12)
        {
            _lblErr.Text = "名字和称呼都控制在 12 字以内（避免气泡装不下）";
            return;
        }
        try
        {
            new DesktopPet.App.Services.JsonPluginStorage(System.IO.Path.Combine(Paths.Data, "plugins", "com.lantern.clipboard"))
                .Set("recordingEnabled", _clipboard.IsChecked == true);
        }
        catch { _lblErr.Text = "无法保存隐私选项，请检查数据目录权限。"; return; }
        _config.KeyboardMirrorEnabled = _keyboard.IsChecked == true;
        _config.AppMode = _config.KeyboardMirrorEnabled ? "work" : "slack";
        _config.UpdateCheckEnabled = _network.IsChecked == true;
        _config.CatName = name;
        _config.OwnerTitle = title;
        ConfigManager.Save(Paths.ConfigFile, _config);   // Save 内部已吞异常，永不抛 → 无需外层 try/catch（v0.1.82 P1-8）
        DialogResult = true;
        Close();
    }

    // ---- 构建器 ----

    private static TextBlock MakeFieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(TextMain),
        Margin = new Thickness(0, 14, 0, 8),
    };

    private static TextBlock MakeFieldHint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = new SolidColorBrush(TextSub),
        Margin = new Thickness(2, 6, 0, 0),
    };

    private static Border WrapField(TextBox tb)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(BgInput),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Child = tb,
        };
        // 聚焦时高亮 accent
        tb.GotFocus += (_, _) => border.BorderBrush = new SolidColorBrush(Accent);
        tb.LostFocus += (_, _) => border.BorderBrush = new SolidColorBrush(BorderC);
        return border;
    }

    private static TextBox MakeInput(string placeholder)
    {
        return new TextBox
        {
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(TextMain),
            CaretBrush = new SolidColorBrush(Accent),
            FontSize = 14,
            BorderThickness = new Thickness(0),
            MaxLength = 12,
            ToolTip = placeholder,
        };
    }

    private static Button MakeButton(string text, bool isPrimary)
    {
        var brush = isPrimary ? new SolidColorBrush(Accent) : new SolidColorBrush(BgCard);
        var fg = isPrimary ? Colors.White : TextMain;
        return new Button
        {
            Content = text,
            Width = 80,
            Height = 34,
            Background = brush,
            Foreground = new SolidColorBrush(fg),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = Cursors.Hand,
        };
    }
}
