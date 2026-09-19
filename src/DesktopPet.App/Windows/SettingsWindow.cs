using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DesktopPet.App.Core;
using DesktopPet.App.Services;
using DesktopPet.Core;

namespace DesktopPet.App.Windows;

/// <summary>
/// 设置页（v0.1.31 重做——参照 BongoCat 设置页深色主题 + 左侧 sidebar + 主区卡片式 toggle）：
///   通用：开机自启、游戏模式
///   猫咪：缩放、透明度、键鼠反应、回到默认位置、鼠标穿透
///   输入：键鼠映射（双模态）、径向菜单热键、老板键、附加热键
///   插件：原 BuildPluginTab 保留
///   关于：版本 + 介绍
/// 改动立即生效并持久化到 data/config.json。
/// </summary>
/// <remarks>
/// v0.1.84（审计 P2-11「超长文件」）：本类已按内聚的功能区域拆成多个 <c>partial</c> 文件。
/// <para><b>⚠️ 拆分纪律：所有【字段】声明（含主题色 <c>readonly Color</c> 字段）一律保留在本文件</b>——
/// C# 规范未定义跨 partial 文件的字段初始化顺序，移动字段有引入顺序依赖 bug 的风险。拆分只移动方法。</para>
/// <list type="bullet">
/// <item><description><c>SettingsWindow.Pages.cs</c> —— 六个设置页的构建（通用 / 猫咪 / 输入 / 径向菜单 / 插件 / 关于）</description></item>
/// <item><description><c>SettingsWindow.Cards.cs</c> —— 卡片 / 控件构建器（toggle / choice / stepper / button / text / skin picker …）</description></item>
/// <item><description><c>SettingsWindow.Theme.cs</c> —— 主题与页面外壳（滚动条样式 / 页头 / 小节标题）</description></item>
/// <item><description><c>SettingsWindow.HotKeys.cs</c> —— 热键编辑卡片</description></item>
/// </list>
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    private readonly PetWindow _pet;
    private readonly AppConfig _config;
    private ContentControl _mainContent = new();
    private Border _sidebarListPanel = new();
    private readonly List<Border> _sideItems = new();
    private string _currentSection = "general";

    // v0.1.50：主题色（深/浅两套，按 Config.Theme 在 ctor 选择）—— 改实例字段（原 static 常量）
    private readonly Color BgRoot;
    private readonly Color BgSide;
    private readonly Color BgSideSel;
    private readonly Color BgCard;
    private readonly Color BgCardHover;
    private readonly Color BorderC;
    private readonly Color Accent;
    private readonly Color TextMain;
    private readonly Color TextSub;
    private readonly Color ToggleOff;
    private readonly Color TitleBar;

    /// <summary>v0.1.50：语言 helper（"zh"/"en"）—— 核心文案双语，其他后续</summary>
    private string T(string zh, string en) => _config.Language == "en" ? en : zh;

    public SettingsWindow(PetWindow pet)
    {
        _pet = pet;
        _config = pet.Config;
        // 主题：浅色 / 深色（默认深色，原 v0.1.31 色板）
        if (_config.Theme == "light")
        {
            BgRoot = Color.FromRgb(0xF5, 0xF7, 0xFA);
            BgSide = Color.FromRgb(0xEC, 0xEF, 0xF5);
            BgSideSel = Color.FromRgb(0xE2, 0xE8, 0xF2);
            BgCard = Color.FromRgb(0xFF, 0xFF, 0xFF);
            BgCardHover = Color.FromRgb(0xF0, 0xF4, 0xFA);
            BorderC = Color.FromRgb(0xD0, 0xD7, 0xE2);
            Accent = Color.FromRgb(0x2B, 0x7F, 0xE0);
            TextMain = Color.FromRgb(0x1F, 0x29, 0x33);
            TextSub = Color.FromRgb(0x5A, 0x64, 0x70);
            ToggleOff = Color.FromRgb(0xB8, 0xC0, 0xCC);
            TitleBar = Color.FromRgb(0xE3, 0xE8, 0xEF);
        }
        else
        {
            BgRoot = Color.FromRgb(0x1E, 0x26, 0x2F);
            BgSide = Color.FromRgb(0x25, 0x2F, 0x3B);
            BgSideSel = Color.FromRgb(0x2A, 0x36, 0x47);
            BgCard = Color.FromRgb(0x2B, 0x36, 0x47);
            BgCardHover = Color.FromRgb(0x33, 0x40, 0x55);
            BorderC = Color.FromRgb(0x38, 0x44, 0x5A);
            Accent = Color.FromRgb(0x4C, 0x9F, 0xFF);
            TextMain = Color.FromRgb(0xE5, 0xE7, 0xEB);
            TextSub = Color.FromRgb(0x9C, 0xA3, 0xAF);
            ToggleOff = Color.FromRgb(0x47, 0x55, 0x69);
            TitleBar = Color.FromRgb(0x1B, 0x22, 0x2A);
        }
        Title = "Neko 设置";
        Width = 780;
        Height = 640;   // v0.1.38：560→640，内容不再挤在一个矮窗口里
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(BgRoot);
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        // v0.1.82：本机是 125% DPI 缩放（物理 2560×1440 / 逻辑 2048×1152），
        // 不做布局取整时 1px 卡片描边与圆角会落在半像素上 → 发虚、发灰。
        // 开启后所有布局尺寸对齐到设备像素，线条和文字都明显更锐利。
        UseLayoutRounding = true;

        BuildLayout();
        ShowSection("general");
    }

    private void BuildLayout()
    {
        // 顶栏：标题 + 版本号
        var titleBar = new Border
        {
            Background = new SolidColorBrush(TitleBar),
            Padding = new Thickness(20, 14, 20, 14),
            Child = new TextBlock
            {
                Text = $"Neko 设置  ·  v{_config.GetType().Assembly.GetName().Version?.ToString(3) ?? "?"}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextMain),
            }
        };

        // 主体：sidebar + content
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Sidebar
        _sidebarListPanel = BuildSidebar();
        Grid.SetColumn(_sidebarListPanel, 0);
        body.Children.Add(_sidebarListPanel);

        // 主内容
        _mainContent = new ContentControl { Margin = new Thickness(0) };
        Grid.SetColumn(_mainContent, 1);
        body.Children.Add(_mainContent);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        root.Children.Add(body);
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        Content = root;
    }

    private Border BuildSidebar()
    {
        var panel = new StackPanel { Background = new SolidColorBrush(BgSide) };
        string[] sections = { "general", "pet", "input", "radial", "plugin", "about" };
        string[] labels = { T("通用", "General"), T("猫咪", "Cat"), T("输入", "Input"), T("径向菜单", "Radial Menu"), T("插件", "Plugins"), T("关于", "About") };   // v0.1.50：双语
        string[] icons = { "⚙", "🐱", "⌨", "◎", "🔌", "ℹ" };   // Segoe UI Symbol / Emoji

        for (int i = 0; i < sections.Length; i++)
        {
            var sec = sections[i];
            var item = new Border
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Padding = new Thickness(20, 12, 12, 12),
                Cursor = Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = icons[i], FontSize = 16, Foreground = new SolidColorBrush(TextSub), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                        new TextBlock { Text = labels[i], FontSize = 13, Foreground = new SolidColorBrush(TextSub), VerticalAlignment = VerticalAlignment.Center },
                    }
                }
            };
            item.MouseLeftButtonUp += (_, _) => ShowSection(sec);
            // v0.1.47：hover/选中态用 ColorAnimation 平滑过渡（150ms），不再硬设新 brush
            var bgBrush = (SolidColorBrush)item.Background;
            var borderBrush = (SolidColorBrush)item.BorderBrush;
            var iconText = (TextBlock)((StackPanel)item.Child!).Children[0];
            var labelText = (TextBlock)((StackPanel)item.Child!).Children[1];
            item.MouseEnter += (_, _) => { if (_currentSection != sec) bgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0x29, 0x33, 0x3F), TimeSpan.FromMilliseconds(150))); };
            item.MouseLeave += (_, _) => { if (_currentSection != sec) bgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Transparent, TimeSpan.FromMilliseconds(150))); };
            _sideItems.Add(item);
            panel.Children.Add(item);
        }
        return new Border { Child = panel, BorderBrush = new SolidColorBrush(BorderC), BorderThickness = new Thickness(0, 0, 1, 0) };
    }

    /// <summary>切换到指定栏（v0.1.39 起改 public：托盘「关于」项要能直接定位到 about 栏）</summary>
    public void ShowSection(string sec)
    {
        _currentSection = sec;
        // 刷新 sidebar 选中态（v0.1.47：ColorAnimation 过渡 150ms，文字色 / 背景 / 蓝条一起动）
        for (int i = 0; i < _sideItems.Count; i++)
        {
            bool selected = (sec == "general" && i == 0) || (sec == "pet" && i == 1) || (sec == "input" && i == 2) || (sec == "radial" && i == 3) || (sec == "plugin" && i == 4) || (sec == "about" && i == 5);
            var item = _sideItems[i];
            var bgBrush = (SolidColorBrush)item.Background;
            var borderBrush = (SolidColorBrush)item.BorderBrush;
            var sp = (StackPanel)item.Child!;
            var iconText = (TextBlock)sp.Children[0];
            var labelText = (TextBlock)sp.Children[1];
            bgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? BgSideSel : Colors.Transparent, TimeSpan.FromMilliseconds(150)));
            borderBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? Accent : Colors.Transparent, TimeSpan.FromMilliseconds(150)));
            iconText.Foreground.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? Accent : TextSub, TimeSpan.FromMilliseconds(150)));
            labelText.Foreground.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? TextMain : TextSub, TimeSpan.FromMilliseconds(150)));
        }

        // v0.1.47：页面切换淡入（新内容 Opacity 0→1 140ms）
        var newContent = sec switch
        {
            "general" => BuildGeneralPage(),
            "pet" => BuildPetPage(),
            "input" => BuildInputPage(),
            "radial" => BuildRadialMenuPage(),
            "plugin" => BuildPluginPage(),
            "about" => BuildAboutPage(),
            _ => new TextBlock { Text = "未知栏" },
        };
        newContent.Opacity = 0;
        _mainContent.Content = newContent;
        newContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void Save() => ConfigManager.Save(Paths.ConfigFile, _config);
}
