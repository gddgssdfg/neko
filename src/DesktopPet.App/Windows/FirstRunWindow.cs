using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopPet.App.Core;

namespace DesktopPet.App.Windows;

/// <summary>
/// v0.1.41 首次启动引导（封包需求核心：首次安装时设置称呼+取名）。
/// 像素风对话框：深色主题（与设置页一致）+ 蓝色 accent。必填校验，未填完不让进入主程序。
/// 取消按钮 = 退出程序（首次都没设置就退出，宁可不让用户"带着没名字的猫"凑合）。
/// </summary>
public sealed class FirstRunWindow : Window
{
    // 主题色（与 SettingsWindow 保持一致——保持自家身份，深色 + 蓝 accent）
    private static readonly Color BgRoot = Color.FromRgb(0x1E, 0x26, 0x2F);
    private static readonly Color BgCard = Color.FromRgb(0x2B, 0x36, 0x47);
    private static readonly Color BgInput = Color.FromRgb(0x21, 0x2A, 0x36);
    private static readonly Color BorderC = Color.FromRgb(0x38, 0x44, 0x5A);
    private static readonly Color Accent = Color.FromRgb(0x4C, 0x9F, 0xFF);
    private static readonly Color TextMain = Color.FromRgb(0xE5, 0xE7, 0xEB);
    private static readonly Color TextSub = Color.FromRgb(0x9C, 0xA3, 0xAF);
    private static readonly Color TextErr = Color.FromRgb(0xE7, 0x4C, 0x3C);

    private readonly AppConfig _config;
    private readonly TextBox _txtName = MakeInput("奶糖/小橘/糖饼…");
    private readonly TextBox _txtTitle = MakeInput("主人/小可爱/老板…");
    private readonly TextBlock _lblErr;

    public FirstRunWindow(AppConfig config)
    {
        _config = config;
        Title = "欢迎养 Neko";
        Width = 460;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(BgRoot);
        ShowInTaskbar = true;        // 引导期间显示在任务栏（首次必须找到窗口）
        Topmost = true;              // 置顶（首启别被猫挡住——猫其实还没出现）

        var root = new Grid { Margin = new Thickness(28) };
        Content = root;

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
            Text = "欢迎养 Neko !",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(TextMain),
            Margin = new Thickness(0, 0, 0, 8),
        });
        // 副标题
        stack.Children.Add(new TextBlock
        {
            Text = "告诉我两件事，我们就开始：",
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
        MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };

        // 引导窗口按 Esc 视为取消（退出程序）
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };

        // 预填（如果有旧值，比如上版本升级时已经填过但字段是空）
        if (!string.IsNullOrWhiteSpace(_config.CatName)) _txtName.Text = _config.CatName;
        if (!string.IsNullOrWhiteSpace(_config.OwnerTitle)) _txtTitle.Text = _config.OwnerTitle;
    }

    private void OnConfirm()
    {
        var name = _txtName.Text?.Trim() ?? "";
        var title = _txtTitle.Text?.Trim() ?? "";
        if (name.Length == 0 || title.Length == 0)
        {
            _lblErr.Text = "两个都填一下哦（首启必填）";
            return;
        }
        if (name.Length > 12 || title.Length > 12)
        {
            _lblErr.Text = "名字和称呼都控制在 12 字以内（避免气泡装不下）";
            return;
        }
        _config.CatName = name;
        _config.OwnerTitle = title;
        try { ConfigManager.Save(Paths.ConfigFile, _config); } catch { }
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
