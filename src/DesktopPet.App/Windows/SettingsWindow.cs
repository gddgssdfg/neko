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
public sealed class SettingsWindow : Window
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

    // ============================ 通用 ============================
    private UIElement BuildGeneralPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(MakeSectionHeader("常规"));   // v0.1.38
        stack.Children.Add(BuildToggleCard(
            "开机自启动 Neko",
            "登录 Windows 后自动运行",
            Autostart.IsEnabled(),
            on => { try { Autostart.SetEnabled(on); _pet.SetStartWithWindows(on); } catch (Exception ex) { MessageBox.Show("开机自启失败：" + ex.Message); } }));
        // 游戏模式用 ChoiceCard 互斥按钮组（v0.1.38：分组标题）
        stack.Children.Add(MakeSectionHeader("游戏模式"));
        int gameIdx = !_config.GameModeEnabled ? 0 : (_config.GameModeAction == "through" ? 2 : 1);
        stack.Children.Add(BuildChoiceCard("游戏模式", "检测到全屏应用时的低干扰策略",
            new[] { "关闭", "全屏时藏猫", "全屏时鼠标穿透" }, gameIdx,
            i =>
            {
                if (i == 0) _pet.SetGameMode(false, _config.GameModeAction);
                else _pet.SetGameMode(true, i == 2 ? "through" : "hide");
            }));
        // v0.1.50：外观与语言（深浅主题 + 中英语言）
        stack.Children.Add(MakeSectionHeader(T("外观与语言", "Appearance & Language")));
        stack.Children.Add(BuildChoiceCard(
            T("界面主题", "Theme"), T("深色适合夜猫子，浅色更明亮", "Dark for night, light for day"),
            new[] { T("深色", "Dark"), T("浅色", "Light") }, _config.Theme == "light" ? 1 : 0,
            i =>
            {
                _config.Theme = i == 1 ? "light" : "dark";
                Save();
                MessageBox.Show(T("主题已保存，重新打开设置窗口生效。", "Theme saved. Reopen settings to apply."));
            }));
        stack.Children.Add(BuildChoiceCard(
            T("界面语言", "Language"), T("中文 / English", "Chinese / English"),
            new[] { "中文", "English" }, _config.Language == "en" ? 1 : 0,
            i =>
            {
                _config.Language = i == 1 ? "en" : "zh";
                Save();
                // v0.1.51：语言即时生效——重建当前栏（页面标题/卡片文案立即变英文；sidebar 下次打开变）
                ShowSection(_currentSection);
            }));
        return WrapPage(stack, T("通用", "General"));
    }

    // ============================ 猫咪 ============================
    private UIElement BuildPetPage()
    {
        var stack = new StackPanel();

        // 外观组（v0.1.38）
        stack.Children.Add(MakeSectionHeader("外观"));
        // 缩放（+ - 步进 0.2）
        stack.Children.Add(BuildStepperCard(
            "缩放倍率", "调整猫的显示大小",
            _config.Scale, 1.0, 8.0, 0.2,
            v =>
            {
                _pet.SetScale(v);
                Save();
            },
            val => $"{val:F1}x  ({32 * val:F0}px)"));

        // 透明度（+ - 步进 0.1）
        stack.Children.Add(BuildStepperCard(
            "窗口透明度", "降低可让猫半透浮在桌面",
            _config.Opacity, 0.2, 1.0, 0.1,
            v =>
            {
                _pet.SetOpacity(v);
                Save();
            },
            val => $"{val * 100:F0}%"));

        // v0.1.74：皮肤选择——扫 neko-assets/*/skin.json 动态列出（pixel/soft + 用户自制皮肤）。
        // 取代 v0.1.48 的写死 pixel/soft 两项；下拉内容随目录变化，宿主无需改动。
        var skins = SkinRegistry.Scan(Paths.Assets);
        var skinList = skins.ToList();
        string curSkin = skinList.Any(s => s.Id == _config.Skin) ? _config.Skin : SkinRegistry.DefaultSkinId;
        var skinOptions = skinList.Select(s => $"{s.DisplayName}（{s.Id}）").ToArray();
        int skinIdx = Math.Max(0, skinList.FindIndex(s => s.Id == curSkin));
        stack.Children.Add(BuildChoiceCardWrapped(
            "外观风格", "皮肤由 neko-assets/<皮肤名>/skin.json 定义；放入新目录即可扩展",
            skinOptions, skinIdx,
            i =>
            {
                var s = skinList[i].Id;
                _pet.SetSkin(s);
                _config.Skin = s;
                Save();
            }));
        stack.Children.Add(BuildButtonCard(
            "皮肤目录", "打开 neko-assets/，放入自制皮肤目录（skin.json + 帧 PNG）后重开本页即可选用",
            "打开皮肤目录",
            () =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = Paths.Assets, UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show("打开皮肤目录失败：" + ex.Message); }
            }));

        // v0.1.41：称呼 + 猫名（封包需求：首启引导后可在设置页改；修改后重建调度器使新台词生效）
        stack.Children.Add(BuildTextCard(
            "猫的名字", "气泡对话、升级提示都会叫这个名字（≤12 字）",
            _config.CatName,
            v => { _config.CatName = v; Save(); _pet.RebuildChatScheduler(); }));
        stack.Children.Add(BuildTextCard(
            "猫怎么称呼你", "出现在休息提醒、闲聊等场景（≤12 字）",
            _config.OwnerTitle,
            v => { _config.OwnerTitle = v; Save(); _pet.RebuildChatScheduler(); }));

        stack.Children.Add(MakeSectionHeader("行为"));
        stack.Children.Add(BuildToggleCard(
            "键鼠反应",
            "打字/点击时猫短暂瞪眼回应",
            _config.KeyboardReactionsEnabled,
            on => { _config.KeyboardReactionsEnabled = on; Save(); }));

        // v0.1.40：气泡对话开关（像素云朵 + 猫主动闲聊）
        stack.Children.Add(BuildToggleCard(
            "气泡对话",
            "猫会偶尔说话（工作久了提醒、随机闲聊，语气随好感度变亲昵）；插件通知也用它显示",
            _config.ChatEnabled,
            on => { _pet.ApplyChatEnabled(on); Save(); }));

        stack.Children.Add(BuildToggleCard(
            "鼠标穿透",
            "纯展示不接收点击（无法拖拽/抚摸）",
            _pet.IsMousePassthrough,
            on => _pet.SetMousePassthrough(on)));

        stack.Children.Add(BuildButtonCard(
            "回到默认位置",
            "清除位置记忆，猫回到右下角",
            "重置", () => _pet.ResetPosition()));

        return WrapPage(stack, "猫咪");
    }

    // ============================ 输入 ============================
    private UIElement BuildInputPage()
    {
        var stack = new StackPanel();

        // 双模态：互斥按钮组（v0.1.38：分组标题）
        stack.Children.Add(MakeSectionHeader("应用模式"));
        int modeIdx = _config.AppMode == "work" ? 1 : 0;
        // v0.1.48：去后缀（摸鱼模式/工作模式）—— 与托盘菜单/右键菜单统一文案
        stack.Children.Add(BuildChoiceCard(
            "应用模式", "摸鱼模式=收起键盘，猫滑到最近的屏幕边缘挂住；工作模式=猫趴在键盘前干活（参照 BongoCat）",
            new[] { "摸鱼模式", "工作模式" }, modeIdx,
            i => { var m = i == 1 ? "work" : "slack"; _config.AppMode = m; _config.KeyboardMirrorEnabled = (m == "work"); Save(); _pet.SetAppMode(m); }));

        // v0.1.39：摸鱼模式的吸附行为可关（有人就喜欢猫待在原地）
        stack.Children.Add(BuildToggleCard(
            "摸鱼时吸附屏幕边缘",
            "切到摸鱼模式后猫滑到最近边缘挂住，鼠标走远也不会掉下来",
            _config.SlackEdgeAttach,
            on => { _config.SlackEdgeAttach = on; Save(); if (on && _config.AppMode == "slack") _pet.SetAppMode("slack"); }));

        // v0.1.39：键盘朝向（朝我 / 朝猫=镜像）
        stack.Children.Add(BuildChoiceCard(
            "键盘朝向", "朝我=Esc 在左上角（你的视角）；朝猫=水平镜像，猫坐在对面看到的布局",
            new[] { "朝我（默认）", "朝猫（镜像）" }, _config.KeyboardMirrorFlipped ? 1 : 0,
            i => { _config.KeyboardMirrorFlipped = i == 1; Save(); _pet.SetKeyboardMirrorFlipped(i == 1); }));

        // 热键编辑；附加热键（透明度/缩放/重置）v0.1.33 起热更新，不用重启
        // v0.1.38：分组标题分隔，避免 7 个热键卡片连排看着像"一堵墙"
        stack.Children.Add(MakeSectionHeader("快捷键"));
        stack.Children.Add(BuildHotKeyCard("径向菜单热键", _config.MenuHotKey, (c, a, s, k) => _pet.SetMenuHotKey(c, a, s, k)));
        stack.Children.Add(BuildHotKeyCard("显示/隐藏猫咪（老板键）", _config.TogglePetHotKey, (c, a, s, k) => _pet.SetTogglePetHotKey(c, a, s, k)));
        stack.Children.Add(BuildHotKeySet("透明度 +10%", _config.OpacityUpHotKey, "opacity_up"));
        stack.Children.Add(BuildHotKeySet("透明度 -10%", _config.OpacityDownHotKey, "opacity_down"));
        stack.Children.Add(BuildHotKeySet("缩放 +20%", _config.ScaleUpHotKey, "scale_up"));
        stack.Children.Add(BuildHotKeySet("缩放 -20%", _config.ScaleDownHotKey, "scale_down"));
        stack.Children.Add(BuildHotKeySet("回到默认位置", _config.ResetPosHotKey, "reset_pos"));

        return WrapPage(stack, "输入");
    }

    // ============================ 径向菜单 ============================
    /// <summary>v0.1.71：径向菜单顺序/显隐/数量上限设置页。让用户自由调整右键菜单里显示哪些插件、顺序如何。</summary>
    private UIElement BuildRadialMenuPage()
    {
        var cfg = _config.RadialMenu;
        var allSlots = _pet.UIApi.AllRadialSlots;

        var stack = new StackPanel();

        // 1) 显示数量上限
        int total = allSlots.Count;
        stack.Children.Add(MakeSectionHeader("显示数量"));
        stack.Children.Add(BuildStepperCard(
            "最多显示槽位数",
            "排序后只显示前 N 个槽位，超出隐藏；0 = 不限（全部显示）",
            cfg.MaxVisible, 0, total, 1,
            v => { cfg.MaxVisible = (int)v; SaveRadialMenu(allSlots); },
            val => val == 0 ? "不限" : $"{val:F0} 个"));

        // 2) 槽位顺序 + 显隐
        stack.Children.Add(MakeSectionHeader("插件顺序与显隐"));
        var hint = new TextBlock
        {
            Text = "用 ▲▼ 调整顺序，开关控制显隐。灰色 = 已隐藏；标注「超出上限」的槽位不会显示。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(TextSub),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        stack.Children.Add(hint);

        // 可见槽位（按当前 Resolve 顺序）+ 隐藏槽位（置灰）
        var visibleList = RadialMenuResolver.Resolve(allSlots, cfg);
        var hiddenList = allSlots.Where(s => !RadialMenuResolver.IsVisible(s, cfg)).ToList();
        // 把「可见但被 MaxVisible 截断」的槽位也归入隐藏展示区（标「超出上限」）
        var visibleIds = new HashSet<string>(visibleList.Select(s => s.Id));
        var overLimit = allSlots.Where(s => RadialMenuResolver.IsVisible(s, cfg) && !visibleIds.Contains(s.Id)).ToList();

        // 上移/下移/显隐的操作落点：操作后重建整个页面（简单可靠，槽位数 ≤32 性能无虞）
        void MoveAndSave(int fromIdx, int toIdx)
        {
            var list = RadialMenuResolver.Resolve(_pet.UIApi.AllRadialSlots, cfg);
            if (fromIdx < 0 || fromIdx >= list.Count || toIdx < 0 || toIdx >= list.Count) return;
            var tmp = list[fromIdx];
            list[fromIdx] = list[toIdx];
            list[toIdx] = tmp;
            RadialMenuResolver.FreezeOrder(list, cfg);
            SaveRadialMenu(_pet.UIApi.AllRadialSlots);
            ShowSection("radial");
        }

        // 可见槽位列表（可上移/下移/隐藏）
        for (int i = 0; i < visibleList.Count; i++)
        {
            var slot = visibleList[i];
            int idx = i;
            bool first = i == 0;
            bool last = i == visibleList.Count - 1;
            stack.Children.Add(BuildSlotRow(slot, idx + 1,
                upEnabled: !first, downEnabled: !last,
                onUp: () => MoveAndSave(idx, idx - 1),
                onDown: () => MoveAndSave(idx, idx + 1),
                isVisible: true,
                onToggleVisible: () =>
                {
                    cfg.Visible[slot.Id] = false;
                    SaveRadialMenu(allSlots);
                    ShowSection("radial");
                },
                extraNote: null));
        }

        // 超出上限的槽位（可见但被 MaxVisible 截断）
        foreach (var slot in overLimit)
        {
            stack.Children.Add(BuildSlotRow(slot, -1,
                upEnabled: false, downEnabled: false, onUp: () => { }, onDown: () => { },
                isVisible: true,
                onToggleVisible: () =>
                {
                    cfg.Visible[slot.Id] = false;
                    SaveRadialMenu(allSlots);
                    ShowSection("radial");
                },
                extraNote: "超出上限（提高上限或调顺序可显示）"));
        }

        // 隐藏槽位（置灰，仅可恢复显示）
        foreach (var slot in hiddenList)
        {
            stack.Children.Add(BuildSlotRow(slot, -1,
                upEnabled: false, downEnabled: false, onUp: () => { }, onDown: () => { },
                isVisible: false,
                onToggleVisible: () =>
                {
                    cfg.Visible[slot.Id] = true;
                    SaveRadialMenu(allSlots);
                    ShowSection("radial");
                },
                extraNote: "已隐藏"));
        }

        // 3) 恢复默认
        stack.Children.Add(MakeSectionHeader("重置"));
        stack.Children.Add(BuildButtonCard("恢复默认", "清空顺序/显隐/数量上限，回到插件默认顺序并全部显示", "恢复默认", () =>
        {
            _config.RadialMenu = new RadialMenuConfig();
            SaveRadialMenu(_pet.UIApi.AllRadialSlots);
            ShowSection("radial");
        }));

        return WrapPage(stack, T("径向菜单", "Radial Menu"));
    }

    /// <summary>径向菜单页统一保存：先 Prune 清残留，再持久化。</summary>
    private void SaveRadialMenu(IReadOnlyList<RadialSlot> allSlots)
    {
        RadialMenuResolver.Prune(allSlots, _config.RadialMenu);
        Save();
    }

    /// <summary>单条槽位卡片：序号 + 标签 + 图标名 + ▲▼ + 显隐开关。</summary>
    private Border BuildSlotRow(RadialSlot slot, int orderNo, bool upEnabled, bool downEnabled,
        Action onUp, Action onDown, bool isVisible, Action onToggleVisible, string? extraNote)
    {
        var dim = !isVisible;
        var labelColor = dim ? new SolidColorBrush(TextSub) : new SolidColorBrush(TextMain);
        var labelOpacity = dim ? 0.55 : 1.0;

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (orderNo > 0)
            left.Children.Add(new TextBlock { Text = $"{orderNo}.", FontSize = 13, Foreground = new SolidColorBrush(TextSub), Width = 28, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = slot.Label, FontSize = 13, Foreground = labelColor, Opacity = labelOpacity, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(label);
        if (!string.IsNullOrEmpty(extraNote))
        {
            var note = new TextBlock { Text = $"  · {extraNote}", FontSize = 11, Foreground = new SolidColorBrush(Accent), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 };
            left.Children.Add(note);
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var upBtn = MakeStepperButton("▲", onUp);
        upBtn.IsEnabled = upEnabled;
        upBtn.Opacity = upEnabled ? 1.0 : 0.3;
        var downBtn = MakeStepperButton("▼", onDown);
        downBtn.IsEnabled = downEnabled;
        downBtn.Opacity = downEnabled ? 1.0 : 0.3;

        var sw = new ToggleSwitch(ToggleOff) { IsOn = isVisible };
        sw.Toggled += on => onToggleVisible();

        right.Children.Add(upBtn);
        right.Children.Add(downBtn);
        right.Children.Add(new Border { Width = 12 });
        right.Children.Add(sw);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn(left, 0);
        grid.Children.Add(right);
        Grid.SetColumn(right, 1);

        return new Border
        {
            Background = new SolidColorBrush(BgCard),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = dim ? 0.85 : 1.0,
            Child = grid,
        };
    }

    // ============================ 插件 ============================
    /// <summary>v0.1.44：插件页改为卡片式——每个插件一张独立卡片（名称+版本/作者/描述/加载错误/内置设置页），
    /// 取代旧 ListBox 左右布局（拥挤、信息挤一列）。卡片风格与设置页其他卡片统一。</summary>
    private UIElement BuildPluginPage()
    {
        var stack = new StackPanel();

        var plugins = _pet.PluginHost.Plugins;
        if (plugins.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（无插件）",
                Foreground = new SolidColorBrush(TextSub),
                FontSize = 13,
                Margin = new Thickness(0, 24, 0, 0),
            });
            return WrapPage(stack, "插件");
        }

        foreach (var p in plugins)
        {
            var sp = new StackPanel();

            // 标题行：名称（左）+ 版本（中）+ v0.1.46 插件开关（右）
            var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.Children.Add(new TextBlock
            {
                Text = p.Manifest.Name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextMain),
            });
            var ver = new TextBlock
            {
                Text = $"v{p.Manifest.Version}",
                FontSize = 11,
                Foreground = new SolidColorBrush(TextSub),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(ver, 1);
            titleRow.Children.Add(ver);

            // v0.1.46：插件启用/禁用开关
            var idCap = p.Manifest.Id;
            bool enabled = _pet.PluginHost.IsEnabled(idCap);
            var toggle = new ToggleSwitch(ToggleOff)   // v0.1.50：主题色 offColor
            {
                IsOn = enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            toggle.Toggled += on =>
            {
                bool ok = on ? _pet.PluginHost.EnablePlugin(idCap) : _pet.PluginHost.DisablePlugin(idCap);
                if (!ok)
                {
                    // 加载失败回滚 UI 状态
                    toggle.IsOn = !on;
                    return;
                }
                if (on) _config.DisabledPlugins.Remove(idCap);
                else if (!_config.DisabledPlugins.Contains(idCap)) _config.DisabledPlugins.Add(idCap);
                Save();
            };
            Grid.SetColumn(toggle, 2);
            titleRow.Children.Add(toggle);
            sp.Children.Add(titleRow);

            // 作者
            sp.Children.Add(new TextBlock
            {
                Text = $"作者：{p.Manifest.Author}",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(TextSub),
                Margin = new Thickness(0, 4, 0, 0),
            });

            // 描述
            sp.Children.Add(new TextBlock
            {
                Text = p.Manifest.Description,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextMain),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            // 加载错误（红色提示行）
            foreach (var (dir, err) in _pet.PluginHost.Errors)
            {
                if (System.IO.Path.GetFileName(dir) == p.Manifest.Id || dir.EndsWith(p.Manifest.Id, System.StringComparison.OrdinalIgnoreCase))
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"加载错误：{err}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0),
                    });
            }

            // 内置设置页（插件注册的 ISettingsPage，嵌入卡内）
            foreach (var page in _pet.UIApi.AllSettingsPages)
            {
                if (page.Title == p.Manifest.Id || page.Title == p.Manifest.Name)
                {
                    try { if (page.BuildContent() is FrameworkElement u) { u.Margin = new Thickness(0, 10, 0, 0); sp.Children.Add(u); } } catch { }
                }
            }

            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(BgCard),
                BorderBrush = new SolidColorBrush(BorderC),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 14, 20, 14),
                Margin = new Thickness(0, 0, 0, 12),
                Child = sp,
            });
        }
        return WrapPage(stack, "插件");
    }

    // ============================ 关于 ============================
    private UIElement BuildAboutPage()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        sp.Children.Add(MakeHeader("Neko 桌宠"));
        sp.Children.Add(new TextBlock
        {
            Text = $"版本：v{_config.GetType().Assembly.GetName().Version?.ToString(3) ?? "?"}\n\n一只轻量像素橘猫，支持插件生态和键鼠可视化。\n\n作者：灯灯（Lantern）\n技术：C# + WPF + .NET 8",
            FontSize = 13, Foreground = new SolidColorBrush(TextMain), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), LineHeight = 22,
        });
        sp.Children.Add(MakeSectionHeader("更新"));
        sp.Children.Add(BuildUpdateCard());
        sp.Children.Add(BuildToggleCard(
            "启用自动检查更新",
            "每天启动时后台静默检查一次；发现新版本用气泡轻提示（不打断你）。关闭后不再自动检查。",
            _config.UpdateCheckEnabled,
            on => { _config.UpdateCheckEnabled = on; Save(); }));

        sp.Children.Add(MakeSectionHeader("快捷键速查"));
        sp.Children.Add(new TextBlock
        {
            Text = "Ctrl+Alt+N        打开径向菜单\nCtrl+Shift+H      显隐猫咪（老板键）\nCtrl+Alt+Shift+↑↓  透明度 ±10%\nCtrl+Alt+Shift+←→  缩放 ±20%\nCtrl+Alt+Shift+R   回到默认位置",
            FontSize = 12, Foreground = new SolidColorBrush(TextSub), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), LineHeight = 26,
        });
        return WrapPage(sp, "关于");
    }

    // ============================ 卡片构建器 ============================

    private UIElement WrapPage(FrameworkElement content, string title)
    {
        var sp = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };   // v0.1.38：内容区左右留白，不再贴边
        sp.Children.Add(MakeHeader(title));
        sp.Children.Add(content);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(BgRoot) };
    }

    private FrameworkElement MakeHeader(string title) => new TextBlock
    {
        Text = title,
        FontSize = 20,   // v0.1.38：标题更大更醒目
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(TextMain),
        Margin = new Thickness(0, 0, 0, 22),
    };

    /// <summary>v0.1.38：分组小节标题（如"快捷键"），与卡片之间拉开距离</summary>
    private FrameworkElement MakeSectionHeader(string title) => new TextBlock
    {
        Text = title,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(TextSub),
        Margin = new Thickness(0, 20, 0, 10),
    };

    private Border BuildToggleCard(string title, string? desc, bool initial, Action<bool> onChange)
    {
        var sw = new ToggleSwitch(ToggleOff) { IsOn = initial };   // v0.1.50：主题色
        sw.Toggled += on => onChange(on);
        return BuildCard(title, desc, sw);
    }

    private Border BuildChoiceCard(string title, string? desc, string[] options, int selectedIdx, Action<int> onChange)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<Border>();
        // v0.1.57(fix 存量 bug)：点击后选中态不刷新。Fix：卡内收集按钮 + RefreshSelection 统一刷新，
        // ColorAnimation 150ms 过渡（与 sidebar 选中 v0.1.47 一致）——按钮背景是 ctor 里 new 的
        // SolidColorBrush（非冻结），可直接 BeginAnimation。
        var unselectedBg = Color.FromRgb(0x3A, 0x44, 0x57);

        void RefreshSelection(int newIdx)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var brush = (SolidColorBrush)buttons[i].Background;
                brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                    new ColorAnimation(newIdx == i ? Accent : unselectedBg, TimeSpan.FromMilliseconds(150)));
            }
        }

        for (int i = 0; i < options.Length; i++)
        {
            int cap = i;
            var btn = new Border
            {
                Background = new SolidColorBrush(i == selectedIdx ? Accent : unselectedBg),
                BorderBrush = new SolidColorBrush(BorderC),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = options[i], Foreground = new SolidColorBrush(TextMain), FontSize = 12 },
            };
            btn.MouseLeftButtonUp += (_, _) =>
            {
                onChange(cap);
                RefreshSelection(cap);
            };
            buttons.Add(btn);
            sp.Children.Add(btn);
        }
        return BuildCard(title, desc, sp);
    }

    /// <summary>
    /// v0.1.74：与 <see cref="BuildChoiceCard"/> 视觉一致，但用 WrapPanel 承载选项——
    /// 供「皮肤」这类数量可随用户扩展（UGC）的选择器使用，避免横向溢出。
    /// </summary>
    private Border BuildChoiceCardWrapped(string title, string? desc, string[] options, int selectedIdx, Action<int> onChange)
    {
        var sp = new WrapPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<Border>();
        var unselectedBg = Color.FromRgb(0x3A, 0x44, 0x57);

        void RefreshSelection(int newIdx)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var brush = (SolidColorBrush)buttons[i].Background;
                brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                    new ColorAnimation(newIdx == i ? Accent : unselectedBg, TimeSpan.FromMilliseconds(150)));
            }
        }

        for (int i = 0; i < options.Length; i++)
        {
            int cap = i;
            var btn = new Border
            {
                Background = new SolidColorBrush(i == selectedIdx ? Accent : unselectedBg),
                BorderBrush = new SolidColorBrush(BorderC),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = options[i], Foreground = new SolidColorBrush(TextMain), FontSize = 12 },
            };
            btn.MouseLeftButtonUp += (_, _) =>
            {
                onChange(cap);
                RefreshSelection(cap);
            };
            buttons.Add(btn);
            sp.Children.Add(btn);
        }
        return BuildCard(title, desc, sp);
    }

    private Border BuildStepperCard(string title, string? desc, double initial, double min, double max, double step, Action<double> onChange, Func<double, string> formatter)
    {
        var valueText = new TextBlock
        {
            Text = formatter(initial),
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Accent),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(valueText);
        var (decBtn, incBtn) = MakeStepperButtons(initial, step, v =>
        {
            var clamped = Math.Clamp(v, min, max);
            valueText.Text = formatter(clamped);
            onChange(clamped);
        });
        sp.Children.Add(decBtn);
        sp.Children.Add(incBtn);
        return BuildCard(title, desc, sp);
    }

    private (Border, Border) MakeStepperButtons(double initial, double step, Action<double> onChange)
    {
        var value = initial;
        var dec = MakeStepperButton("−", () => { value -= step; onChange(value); });
        var inc = MakeStepperButton("+", () => { value += step; onChange(value); });
        return (dec, inc);
    }

    private Border MakeStepperButton(string label, Action onClick)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x57)),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Width = 32, Height = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = label, FontSize = 16, Foreground = new SolidColorBrush(TextMain), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    private Border BuildButtonCard(string title, string? desc, string btnText, Action onClick)
    {
        var btn = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x57)),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = btnText, Foreground = new SolidColorBrush(TextMain), FontSize = 13 },
        };
        btn.MouseLeftButtonUp += (_, _) => onClick();
        return BuildCard(title, desc, btn);
    }

    /// <summary>
    /// v0.1.77：「检查更新」按钮卡片——点击后异步查 Gitee；
    /// 检查中按钮禁用 + 文案变「检查中…」（防狂点）；结果用 MessageBox 反馈。
    /// 有新版本 → 「打开下载页」；无 → 「已是最新」；失败 → 「请稍后重试」。
    /// </summary>
    private Border BuildUpdateCard()
    {
        var btn = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x57)),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "检查更新", Foreground = new SolidColorBrush(TextMain), FontSize = 13 },
        };
        var label = (TextBlock)btn.Child;
        var busy = false;
        btn.MouseLeftButtonUp += async (_, _) =>
        {
            if (busy) return;                 // 防狂点
            busy = true;
            btn.IsEnabled = false;            // Border 禁用后会吞掉后续点击
            btn.Opacity = 0.6;
            label.Text = "检查中…";
            try
            {
                var latest = await UpdateService.FetchLatestAsync(CancellationToken.None);
                if (latest is null)
                {
                    MessageBox.Show(T("检查失败，请稍后重试（网络不可用，或仓库暂未发布 Release）。", "Check failed. Please try again later."),
                        "Neko", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (UpdateService.IsNewer(UpdateService.CurrentVersion, latest.Version))
                {
                    var r = MessageBox.Show(
                        T($"发现新版本 {latest.TagName}（当前 v{UpdateService.CurrentVersion}）。\n\n是否打开下载页？",
                          $"New version {latest.TagName} found (current v{UpdateService.CurrentVersion}).\n\nOpen the download page?"),
                        "Neko", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r == MessageBoxResult.Yes) OpenUrl(latest.ReleaseUrl);
                }
                else
                {
                    MessageBox.Show(T($"已是最新版本（v{UpdateService.CurrentVersion}）。", $"You're on the latest version (v{UpdateService.CurrentVersion})."),
                        "Neko", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            finally
            {
                busy = false;
                btn.IsEnabled = true;
                btn.Opacity = 1.0;
                label.Text = "检查更新";
            }
        };
        return BuildCard(T("检查更新", "Check for updates"), T("手动检查是否有新版本；只提示，不会自动安装。", "Manually check for a newer version; it only notifies — never auto-installs."), btn);
    }

    /// <summary>用系统默认浏览器打开链接；失败弹可读提示（不抛）。</summary>
    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("发布页地址为空，无法打开。", "Neko", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开链接失败：" + ex.Message, "Neko", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>v0.1.41：文本输入卡片（称呼/猫名等）—— 标题/副/输入框/保存按钮（回车也保存）</summary>
    private Border BuildTextCard(string title, string? desc, string initial, Action<string> onSave)
    {
        var input = new TextBox
        {
            Text = initial ?? "",
            Background = new SolidColorBrush(Color.FromRgb(0x21, 0x2A, 0x36)),
            Foreground = new SolidColorBrush(TextMain),
            CaretBrush = new SolidColorBrush(Accent),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Width = 140,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            MaxLength = 12,
        };
        // 聚焦高亮 accent
        var inputBorder = input;
        input.GotFocus += (_, _) => input.BorderBrush = new SolidColorBrush(Accent);
        input.LostFocus += (_, _) => input.BorderBrush = new SolidColorBrush(BorderC);

        var btn = new Border
        {
            Background = new SolidColorBrush(Accent),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "保存", Foreground = new SolidColorBrush(Colors.White), FontSize = 13, FontWeight = FontWeights.SemiBold },
        };
        btn.MouseLeftButtonUp += (_, _) => onSave(input.Text?.Trim() ?? "");
        // 回车直接保存（不离开焦点更顺手）
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { onSave(input.Text?.Trim() ?? ""); e.Handled = true; } };

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(input);
        row.Children.Add(btn);
        return BuildCard(title, desc, row);
    }

    private Border BuildCard(string title, string? description, FrameworkElement control)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextMain) });
        if (description != null)
            sp.Children.Add(new TextBlock { Text = description, FontSize = 11.5, Foreground = new SolidColorBrush(TextSub), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(sp);
        Grid.SetColumn(sp, 0);
        grid.Children.Add(control);
        Grid.SetColumn(control, 1);
        return new Border
        {
            Background = new SolidColorBrush(BgCard),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 16),   // v0.1.38：内边距加大，文字不再贴卡边
            Margin = new Thickness(0, 0, 0, 14),       // v0.1.38：卡片间距 8→14
            Child = grid,
        };
    }

    private Border BuildHotKeySet(string title, HotKeyConfig current, string id)
    {
        // v0.1.33：附加热键直接调 _pet.SetExtraHotKey(id, ...)，热更新不用重启
        return BuildHotKeyCard(title, current, (c, a, s, k) => _pet.SetExtraHotKey(id, c, a, s, k));
    }

    private Border BuildHotKeyCard(string title, HotKeyConfig current, Action<bool, bool, bool, string> onSave)
    {
        // 三 Toggle + TextBox + 保存
        var ctrlSw = new ToggleSwitch(ToggleOff) { IsOn = current.Ctrl };   // v0.1.50：主题色
        var altSw = new ToggleSwitch(ToggleOff) { IsOn = current.Alt };
        var shiftSw = new ToggleSwitch(ToggleOff) { IsOn = current.Shift };
        var keyBox = new TextBox
        {
            Text = current.Key,
            MaxLength = 1,
            Width = 36, FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x22, 0x2A)),
            Foreground = new SolidColorBrush(TextMain),
            BorderBrush = new SolidColorBrush(BorderC),
            CaretBrush = new SolidColorBrush(Accent),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),   // v0.1.38：6→10
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(WrapToggleWithLabel("Ctrl", ctrlSw));
        sp.Children.Add(WrapToggleWithLabel("Alt", altSw, 12, 0, 0, 0));     // v0.1.38：6→12
        sp.Children.Add(WrapToggleWithLabel("Shift", shiftSw, 12, 0, 0, 0));
        sp.Children.Add(keyBox);
        var saveBtn = MakeStepperButton("✓", () =>
        {
            var k = string.IsNullOrWhiteSpace(keyBox.Text) ? current.Key : keyBox.Text[..1].ToUpperInvariant();
            onSave(ctrlSw.IsOn, altSw.IsOn, shiftSw.IsOn, k);
        });
        saveBtn.Width = 36; saveBtn.Margin = new Thickness(10, 0, 0, 0);
        sp.Children.Add(saveBtn);
        return BuildCard(title, "输入一个字母主键，保存后全局立即生效", sp);
    }

    private FrameworkElement WrapToggleWithLabel(string label, ToggleSwitch sw, double l = 0, double t = 0, double r = 0, double b = 0)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(l, t, r, b), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(sw);
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(TextSub), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });  // v0.1.38：4→6
        return sp;
    }

    private void Save() => ConfigManager.Save(Paths.ConfigFile, _config);

    // ============================ ToggleSwitch 控件 ============================

    /// <summary>v0.1.31 自定义深色 toggle 开关（40x22 胶囊 + 18px 圆点 + 0.15s 滑动动画）；v0.1.50 支持主题色（offColor 参数）</summary>
    private sealed class ToggleSwitch : Border
    {
        public event Action<bool>? Toggled;
        private bool _isOn;
        private readonly Ellipse _knob;
        private static readonly Brush OnColor = new SolidColorBrush(Color.FromRgb(0x4C, 0x9F, 0xFF));
        private readonly Brush _offColor;

        public bool IsOn
        {
            get => _isOn;
            set { if (_isOn == value) return; _isOn = value; UpdateVisual(); }
        }

        public ToggleSwitch(Color offColor)
        {
            _offColor = new SolidColorBrush(offColor);
            Width = 40; Height = 22; CornerRadius = new CornerRadius(11);
            BorderThickness = new Thickness(0);
            Background = _offColor;
            Cursor = Cursors.Hand;
            _knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), RenderTransform = new TranslateTransform() };
            Child = new Grid { Children = { _knob } };
            MouseLeftButtonUp += (_, _) => { IsOn = !_isOn; Toggled?.Invoke(_isOn); };
        }

        private void UpdateVisual()
        {
            Background = _isOn ? OnColor : _offColor;
            // 圆点滑到另一侧（动画 0.15s）
            var anim = new DoubleAnimation(_isOn ? 18 : 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            ((TranslateTransform)_knob.RenderTransform!).BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }
}
