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
/// 改动立即生效并持久化到 数据根/config.json。
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

    // ---- v0.1.88：切页结果缓存（灯灯反馈「设置菜单处还不够流畅」）----
    // 真因：每次 ShowSection 都整页重建 UI 树（pet 首建 ~40ms / plugin ~44ms，切回仍 ~13ms）。
    // 缓存已构建的整页 → 切回已访问页 O(1) 复用（≈0ms）；仅在【内容确实会变】时失效。
    // 失效触发点：
    //   · 语言切换 / 主题切换  → InvalidateAllPages()（Pages.cs 对应 ChoiceCard 回调）
    //   · 插件启停（可能增删插件设置页 + 该插件的径向槽位）→ InvalidatePage("plugin") + InvalidatePage("radial")
    //   · 径向菜单改顺序/显隐/上限/恢复默认 → RebuildSection("radial")（Pages.cs 各操作回调）
    //   · 宿主径向注册表运行期真变更（插件增删槽位/改标签）→ OnRadialRegistryChanged() 只失效 radial
    //       （v0.1.89：订阅 UIApiImpl.RadialRegistryChanged，只在【可见字段真的变了】时触发；OnClosed 退订）
    //   · 皮肤目录增删改（进程外）／全局热键改缩放·透明度／游戏模式改鼠标穿透／托盘改应用模式
    //       → PageSignature() 每次进页轻量比对（pet / input 两页）
    private readonly Dictionary<string, CachedPage> _pageCache = new();

    /// <summary>
    /// v0.1.88：缓存页条目。<c>Element</c> 是构建好的整页（含 <see cref="ScrollViewer"/>）；
    /// <c>Signature</c> 是构建时的「外部资源签名」（见 <see cref="PageSignature"/>），
    /// 进页时比对：不一致即判失效并重建。
    /// </summary>
    private sealed record CachedPage(UIElement Element, string Signature);

    /// <summary>
    /// v0.1.89：窗口是否已开始关闭（<see cref="OnClosed"/> 首行置位）。
    /// 仅用于 <see cref="OnRadialRegistryChanged"/> 的竞态护栏——丢弃"退订那一瞬间仍在途"的事件回调。
    /// <para>为什么不能只靠 <see cref="UIElement.IsLoaded"/>：对 <see cref="Window"/> 而言 <c>Closed</c> 先于 <c>Unloaded</c> 触发，
    /// 关闭瞬间 <c>IsLoaded</c> 可能仍为 <c>true</c>，不足以判定"已关闭"。</para>
    /// <para><c>volatile</c>：写发生在 UI 线程（<see cref="OnClosed"/>），读可能发生在插件线程
    /// （事件在插件定时器线程触发时）。与 <c>HourlyChimePlugin._enabled</c> 同一模式——保证"关闭置位"对插件线程立即可见，
    /// 不给"已关闭后仍读到旧值"留理论窗口（即便读到旧值，封送回 UI 线程后的二次判定仍会拦下，故本条为加固而非必需）。</para>
    /// </summary>
    private volatile bool _closed;

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
    private Color AccentText => _config.Theme == "light" ? Accent : Color.FromRgb(0xEE, 0xB5, 0x8F);

    /// <summary>v0.1.50：语言 helper（"zh"/"en"）—— 核心文案双语，其他后续</summary>
    private string T(string zh, string en) => _config.Language == "en" ? en : zh;

    public SettingsWindow(PetWindow pet)
    {
        _pet = pet;
        _config = pet.Config;
        // 主题：浅色 / 深色（默认深色，原 v0.1.31 色板）
        if (_config.Theme == "light")
        {
            BgRoot = Color.FromRgb(0xFA, 0xF7, 0xF2);
            BgSide = Color.FromRgb(0xF1, 0xEB, 0xE3);
            BgSideSel = Color.FromRgb(0xE8, 0xDC, 0xCD);
            BgCard = Color.FromRgb(0xFF, 0xFF, 0xFF);
            BgCardHover = Color.FromRgb(0xF1, 0xEB, 0xE3);
            BorderC = Color.FromRgb(0xDE, 0xD5, 0xC9);
            Accent = Color.FromRgb(0x9A, 0x4D, 0x2B);
            TextMain = Color.FromRgb(0x32, 0x2B, 0x26);
            TextSub = Color.FromRgb(0x76, 0x68, 0x5D);
            ToggleOff = Color.FromRgb(0xB8, 0xC0, 0xCC);
            TitleBar = Color.FromRgb(0xFA, 0xF7, 0xF2);
        }
        else
        {
            BgRoot = Color.FromRgb(0x22, 0x24, 0x23);
            BgSide = Color.FromRgb(0x1B, 0x1E, 0x1C);
            BgSideSel = Color.FromRgb(0x3A, 0x32, 0x29);
            BgCard = Color.FromRgb(0x2B, 0x2F, 0x2C);
            BgCardHover = Color.FromRgb(0x36, 0x3C, 0x37);
            BorderC = Color.FromRgb(0x47, 0x4D, 0x46);
            Accent = Color.FromRgb(0xA9, 0x59, 0x31);
            TextMain = Color.FromRgb(0xF6, 0xF0, 0xE5);
            TextSub = Color.FromRgb(0xB8, 0xB7, 0xAC);
            ToggleOff = Color.FromRgb(0x59, 0x63, 0x59);
            TitleBar = Color.FromRgb(0x22, 0x24, 0x23);
        }
        Title = T("Neko 设置", "Neko Settings");
        Width = Math.Min(940, SystemParameters.WorkArea.Width);
        Height = Math.Min(740, SystemParameters.WorkArea.Height);
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

        // v0.1.89：订阅宿主「径向注册表真变更」——插件运行期增删槽位 / 改标签时只失效 radial 页缓存。
        // 放在 ctor 最末：构建失败则不订阅，避免把半成品窗口钉在事件上。
        _pet.UIApi.RadialRegistryChanged += OnRadialRegistryChanged;
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
                Text = T("NEKO  /  陪伴你的每一天", "NEKO  /  A little company, every day"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextSub),
            }
        };

        // 主体：sidebar + content
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
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
        var brand = new StackPanel { Margin = new Thickness(22, 24, 16, 30) };
        var mascotPath = System.IO.Path.Combine(Paths.Assets, "amber", "thumb.png");
        if (System.IO.File.Exists(mascotPath))
        {
            try
            {
                var mascot = new System.Windows.Media.Imaging.BitmapImage();
                mascot.BeginInit();
                mascot.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                mascot.UriSource = new Uri(mascotPath, UriKind.Absolute);
                mascot.EndInit();
                mascot.Freeze();
                brand.Children.Add(new Image { Source = mascot, Width = 84, Height = 84,
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) });
            }
            catch (Exception ex) { Log.Warn($"设置页品牌缩略图加载失败：{ex.Message}"); }
        }
        brand.Children.Add(new TextBlock { Text = "Neko", FontSize = 28, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(TextMain) });
        brand.Children.Add(new TextBlock { Text = T("你的小小桌面伙伴", "Your desktop companion"), FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(TextSub) });
        panel.Children.Add(brand);
        string[] sections = { "general", "pet", "input", "radial", "plugin", "about" };
        string[] labels = { T("通用", "General"), T("猫咪", "Cat"), T("输入", "Input"), T("径向菜单", "Radial Menu"), T("插件", "Plugins"), T("关于", "About") };   // v0.1.50：双语
        string[] icons = { "\uE713", "\uE8D4", "\uE765", "\uE74C", "\uE71B", "\uE946" };

        for (int i = 0; i < sections.Length; i++)
        {
            var sec = sections[i];
            var item = new Border
            {
                Background = new SolidColorBrush(BgSide),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(10, 2, 10, 2),
                Focusable = true,
                ToolTip = labels[i],
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Padding = new Thickness(14, 13, 10, 13),
                Cursor = Cursors.Hand,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = icons[i], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = new SolidColorBrush(TextSub), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                        new TextBlock { Text = labels[i], FontSize = 13, Foreground = new SolidColorBrush(TextSub), VerticalAlignment = VerticalAlignment.Center },
                    }
                }
            };
            item.MouseLeftButtonUp += (_, _) => ShowSection(sec);
            item.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ShowSection(sec); e.Handled = true; } };
            // v0.1.47：hover/选中态用 ColorAnimation 平滑过渡（150ms），不再硬设新 brush
            var bgBrush = (SolidColorBrush)item.Background;
            var borderBrush = (SolidColorBrush)item.BorderBrush;
            var iconText = (TextBlock)((StackPanel)item.Child!).Children[0];
            var labelText = (TextBlock)((StackPanel)item.Child!).Children[1];
            item.MouseEnter += (_, _) => { if (_currentSection != sec) bgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, new ColorAnimation(BgCardHover, TimeSpan.FromMilliseconds(150))); };
            item.MouseLeave += (_, _) => { if (_currentSection != sec) bgBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, new ColorAnimation(BgSide, TimeSpan.FromMilliseconds(150))); };
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
                new ColorAnimation(selected ? BgSideSel : BgSide, TimeSpan.FromMilliseconds(150)));
            borderBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? Accent : Colors.Transparent, TimeSpan.FromMilliseconds(150)));
            iconText.Foreground.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? AccentText : TextSub, TimeSpan.FromMilliseconds(150)));
            labelText.Foreground.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new ColorAnimation(selected ? TextMain : TextSub, TimeSpan.FromMilliseconds(150)));
        }

        // v0.1.47：页面切换淡入（新内容 Opacity 0→1 140ms）
        // v0.1.88：先查缓存——命中且外部资源未变则直接复用（切回已访问页 ≈0ms），否则重建并写回缓存。
        var sig = PageSignature(sec);
        if (!_pageCache.TryGetValue(sec, out var cached) || cached.Signature != sig)
        {
            cached = new CachedPage(BuildPage(sec), sig);
            _pageCache[sec] = cached;
        }
        var newContent = cached.Element;
        newContent.Opacity = 0;
        _mainContent.Content = newContent;
        newContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>v0.1.88：按 section 构建对应页（从 <see cref="ShowSection"/> 抽出，供缓存复用）。</summary>
    private UIElement BuildPage(string sec) => sec switch
    {
        "general" => BuildGeneralPage(),
        "pet" => BuildPetPage(),
        "input" => BuildInputPage(),
        "radial" => BuildRadialMenuPage(),
        "plugin" => BuildPluginPage(),
        "about" => BuildAboutPage(),
        _ => new TextBlock { Text = "未知栏" },
    };

    /// <summary>
    /// v0.1.88：缓存判活用的「外部/旁路状态签名」——页面会展示、但可由【本页控件之外】改动的状态。
    /// <list type="bullet">
    /// <item><description><c>pet</c>：皮肤目录（进程外增删/改 skin.json·thumb.png）+ 缩放/透明度（全局热键
    /// <c>scale_up/down</c>·<c>opacity_up/down</c> 直接改 config）+ 鼠标穿透（进入游戏模式内部会改）。</description></item>
    /// <item><description><c>input</c>：应用模式（托盘菜单「工作/摸鱼模式」经 <c>SetAppMode</c> 直接改 config）。</description></item>
    /// </list>
    /// <para>每次进页比对该轻量签名；不一致即判失效并重建。其余页的失效由各自交互回调显式调用
    /// <see cref="InvalidatePage"/> / <see cref="InvalidateAllPages"/> / <see cref="RebuildSection"/>。</para>
    /// <para>为什么必须带这些值：全局热键 / 托盘菜单在设置窗打开时也会改配置，而缓存页不会自愈
    /// （v0.1.88 缓存引入的回归——修前每次切页重建所以总会读到新值）。</para>
    /// </summary>
    private string PageSignature(string sec) => sec switch
    {
        "pet" => ComputeSkinSignature()
                 + "|scale=" + _config.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + "|opacity=" + _config.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + "|passthrough=" + (_pet.IsMousePassthrough ? '1' : '0'),
        "input" => "mode=" + (_config.AppMode ?? ""),
        _ => "",
    };

    /// <summary>失效单页缓存（下次进入时重建）。</summary>
    private void InvalidatePage(string sec) => _pageCache.Remove(sec);

    /// <summary>失效全部页缓存（文案 / 主题等全局变化时用）。</summary>
    private void InvalidateAllPages() => _pageCache.Clear();

    /// <summary>失效指定栏并【立即重建】（如径向菜单改顺序后必须马上反映新顺序）。</summary>
    private void RebuildSection(string sec)
    {
        InvalidatePage(sec);
        ShowSection(sec);
    }

    /// <summary>
    /// v0.1.89：宿主径向注册表真变更 → <b>只</b>失效 radial 页缓存（下次进入时重建），不动其它页。
    /// <para>事件可能来自插件线程（如定时器刷新槽位文案）→ 先 marshal 到 UI 线程再动
    /// <c>_pageCache</c>（<see cref="Dictionary{TKey,TValue}"/> 非线程安全）。</para>
    /// </summary>
    private void OnRadialRegistryChanged()
    {
        // v0.1.89 竞态护栏：窗口【已开始关闭】→ 不做任何事（丢弃"退订那一瞬间仍在途"的一发事件）。
        //   ⚠️ 此处只读 volatile bool 字段 _closed（写：UI 线程 OnClosed；读：任意线程，含插件线程）—— 可安全读取；
        //      volatile 只做跨线程可见性加固，值语义与普通 bool 一致。
        //      绝不能在此处（封送前）读 IsLoaded：IsLoaded 背后是 DependencyProperty(LoadedPending)，
        //      读它会 VerifyAccess 校验线程亲和性；本事件【可能在插件线程触发】，跨线程读 IsLoaded 会抛
        //      InvalidOperationException("调用线程无法访问此对象")，并冒泡回插件的 RegisterRadialSlot 调用点。
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnRadialRegistryChanged));
            return;
        }
        // 已到 UI 线程：此时读 IsLoaded 才安全。未 Loaded（构建期）/ 已 Unloaded → 同样不该动缓存。
        if (!IsLoaded) return;
        InvalidatePage("radial");
    }

    /// <summary>
    /// v0.1.89：窗口关闭必须<b>退订</b>宿主事件——<see cref="UIApiImpl"/> 生命周期与 App 等长，
    /// 不退订会把本窗口对象永久钉在订阅列表上（本项目对内存 / 事件未解绑泄漏极敏感）。
    /// 订阅期 == 窗口存活期（ctor 订阅、OnClosed 退订）。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;   // 先置位（早于退订）：关闭瞬间仍在途的一发事件也会被首行护栏拦下
        _pet.UIApi.RadialRegistryChanged -= OnRadialRegistryChanged;
        base.OnClosed(e);
    }

    /// <summary>
    /// v0.1.88：皮肤目录签名 —— 目录名集合 + <c>skin.json</c> / <c>thumb.png</c> 的最后写入时间。
    /// <para>任一皮肤目录新增 / 删除，或皮肤清单 / 缩略图被改写 → 签名变化 → pet 页缓存判为失效并重建，
    /// 保证「新增或改一个皮肤目录后再切回 pet 页显示的是新内容」。</para>
    /// <para>IO 异常时返回唯一值（强制本次重建）——宁重建也不显示旧内容。</para>
    /// </summary>
    private static string ComputeSkinSignature()
    {
        try
        {
            var root = Paths.Assets;
            if (!System.IO.Directory.Exists(root)) return "<no-assets>";
            var dirs = System.IO.Directory.GetDirectories(root);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);   // 目录枚举顺序不保证稳定 → 排序后再拼签名
            var sb = new System.Text.StringBuilder(256);
            foreach (var dir in dirs)
            {
                var json = System.IO.Path.Combine(dir, "skin.json");
                if (!System.IO.File.Exists(json)) continue;       // 非皮肤目录（icons/mirror/reference…）
                var thumb = System.IO.Path.Combine(dir, "thumb.png");
                long jt = System.IO.File.GetLastWriteTimeUtc(json).Ticks;
                long tt = System.IO.File.Exists(thumb) ? System.IO.File.GetLastWriteTimeUtc(thumb).Ticks : 0L;
                sb.Append(System.IO.Path.GetFileName(dir)).Append('|').Append(jt).Append('|').Append(tt).Append(';');
            }
            return sb.ToString();
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private void Save() => ConfigManager.Save(Paths.ConfigFile, _config);
}
