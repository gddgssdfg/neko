// v0.1.84：从 SettingsWindow.cs 按功能区域拆出（审计 P2-11）——六个设置页的构建
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

public sealed partial class SettingsWindow : Window
{
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
                // v0.1.88：主题色在构造时按 Config.Theme 选定（改主题需重开窗口才换色），
                // 但缓存里存的是旧主题构建的整页 → 清空缓存，保证重开后不会残留旧主题页（幂等兜底）。
                InvalidateAllPages();
                MessageBox.Show(T("主题已保存，重新打开设置窗口生效。", "Theme saved. Reopen settings to apply."));
            }));
        stack.Children.Add(BuildChoiceCard(
            T("界面语言", "Language"), T("中文 / English", "Chinese / English"),
            new[] { "中文", "English" }, _config.Language == "en" ? 1 : 0,
            i =>
            {
                _config.Language = i == 1 ? "en" : "zh";
                Save();
                // v0.1.88：中/英文案几乎遍布各页（标题/卡片/小节），缓存页全部作废再重建当前栏，
                // 否则切到任何已访问页都会显示旧语言的缓存内容（正确性 bug）。
                InvalidateAllPages();
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
            val => $"{val:F1}x  ({32 * val:F0}px)",
            () => _config.Scale, out var refreshScaleCard));

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
        // v0.1.82：外观选择改成「一行一个皮肤 + 预览缩略图」。
        // 原来用 WrapPanel 标签，皮肤变多（8 套）后挤成一团、而且完全看不出这套皮肤长什么样。
        stack.Children.Add(BuildSkinPicker(skinList, curSkin, s =>
        {
            _pet.SetSkin(s);
            if (!string.Equals(_config.Skin, s, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("该皮肤缺少可用的待机素材，已回退默认皮肤。请检查 skin.json、PNG 尺寸和数据目录下的日志。", "皮肤加载失败");
                RebuildSection("pet");
            }
            Save();
            // v0.1.81：切换皮肤可能自动套用推荐缩放（改了 _config.Scale）——
            // 同步「缩放倍率」卡片显示，避免设置页显示值与真实值不一致。
            refreshScaleCard();
        }));
        stack.Children.Add(BuildButtonCard(
            "刷新皮肤", "新增或替换素材后重新扫描，并重新加载当前皮肤，无需重启。",
            "刷新皮肤", () => { _pet.SetSkin(_config.Skin, forceReload: true); RebuildSection("pet"); }));
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
            v => { cfg.MaxVisible = (int)v; SaveRadialMenu(allSlots); RebuildSection("radial"); },   // v0.1.88：改了上限必须重建（否则落到「超出上限」分区的槽位不刷新）
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
        // v0.1.88：必须走 RebuildSection（先失效缓存再重建）——否则会命中切页缓存显示旧顺序/旧显隐。
        void MoveAndSave(int fromIdx, int toIdx)
        {
            var list = RadialMenuResolver.Resolve(_pet.UIApi.AllRadialSlots, cfg);
            if (fromIdx < 0 || fromIdx >= list.Count || toIdx < 0 || toIdx >= list.Count) return;
            var tmp = list[fromIdx];
            list[fromIdx] = list[toIdx];
            list[toIdx] = tmp;
            RadialMenuResolver.FreezeOrder(list, cfg);
            SaveRadialMenu(_pet.UIApi.AllRadialSlots);
            RebuildSection("radial");
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
                    RebuildSection("radial");
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
                    RebuildSection("radial");
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
                    RebuildSection("radial");
                },
                extraNote: "已隐藏"));
        }

        // 3) 恢复默认
        stack.Children.Add(MakeSectionHeader("重置"));
        stack.Children.Add(BuildButtonCard("恢复默认", "清空顺序/显隐/数量上限，回到插件默认顺序并全部显示", "恢复默认", () =>
        {
            _config.RadialMenu = new RadialMenuConfig();
            SaveRadialMenu(_pet.UIApi.AllRadialSlots);
            RebuildSection("radial");
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

        var sw = new ToggleSwitch(ToggleOff, Accent) { IsOn = isVisible };
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

        stack.Children.Add(BuildButtonCard("接入插件", "放入插件目录后刷新列表并启用；更新已有插件前先禁用，复制新文件后再启用。", "刷新插件列表", () => RebuildSection("plugin")));
        stack.Children.Add(BuildButtonCard("插件目录", Paths.Plugins, "打开插件目录", () =>
        {
            try { System.IO.Directory.CreateDirectory(Paths.Plugins); Process.Start(new ProcessStartInfo { FileName = Paths.Plugins, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("打开插件目录失败：" + ex.Message); }
        }));
        var plugins = _pet.PluginHost.GetInstalledPlugins();
        if (plugins.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "（无插件）",
                Foreground = new SolidColorBrush(TextSub),
                FontSize = 13,
                Margin = new Thickness(0, 24, 0, 0),
            });
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
            bool enabled = _pet.PluginHost.Plugins.Any(loaded => string.Equals(loaded.Manifest.Id, idCap, StringComparison.OrdinalIgnoreCase));
            var toggle = new ToggleSwitch(ToggleOff, Accent)   // v0.1.50：主题色 offColor
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
                    MessageBox.Show(_pet.PluginHost.Errors.LastOrDefault(e => string.Equals(System.IO.Path.GetFileName(e.dir), idCap, StringComparison.OrdinalIgnoreCase)).error ?? "插件加载失败，请查看日志。", "插件加载失败");
                    return;
                }
                if (on) _config.DisabledPlugins.RemoveAll(id => string.Equals(id, idCap, StringComparison.OrdinalIgnoreCase));
                else if (!_config.DisabledPlugins.Contains(idCap, StringComparer.OrdinalIgnoreCase)) _config.DisabledPlugins.Add(idCap);
                Save();
                // v0.1.88：启停插件可能增删其注册的设置页（内嵌在插件卡内）→ 插件页缓存作废，
                // 下次进入 plugin 页重建以反映新的插件集合/设置页。
                InvalidatePage("plugin");
                // v0.1.88：同一个 UnregisterAll/注册流程还会增删该插件的【径向槽位】（AllRadialSlots 28↔27），
                // 径向页展示槽位列表 → 一并作废，否则切回径向页会显示旧槽位集合（QA 复验 M2）。
                InvalidatePage("radial");
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
                if (string.Equals(System.IO.Path.GetFileName(dir), p.Manifest.Id, StringComparison.OrdinalIgnoreCase))
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
                    try { if (page.BuildContent() is FrameworkElement u) { ApplyPluginTheme(u); u.Margin = new Thickness(0, 10, 0, 0); sp.Children.Add(u); } }
                    catch (Exception ex)
                    {
                        // v0.1.82 P1-8：插件设置页构建失败（插件 bug）会让该页整块消失，必须留痕以便定位
                        Log.Warn($"[设置] 插件设置页构建失败：「{page.Title}」- {ex.GetType().Name}: {ex.Message}");
                    }
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
        foreach (var (dir, error) in _pet.PluginHost.Errors.Where(e => !plugins.Any(p => string.Equals(p.PluginDir, e.dir, StringComparison.OrdinalIgnoreCase))))
            stack.Children.Add(new TextBlock
            {
                Text = $"{System.IO.Path.GetFileName(dir)}：{error}", TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)), Margin = new Thickness(0, 0, 0, 12),
            });
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
        sp.Children.Add(MakeSectionHeader(T("数据与存档", "Data & Storage")));
        // v0.2.0：数据根在 v0.1.98 从「exe 同级 data/」搬到了 %AppData%\Neko —— 用户从此在界面上
        // 找不到自己的数据（配置/好感度/插件数据/日志）。这张卡片把「在哪」和「怎么打开」直接给出来，
        // 并注明来源（默认 / 便携 / 环境变量），省掉"我的好感度去哪了"这类提问。
        sp.Children.Add(BuildButtonCard(
            T("数据目录", "Data folder"),
            $"{T("配置、好感度、插件数据与日志都在这里（v0.1.98 起不再放在程序目录）", "Config, affection, plugin data and logs live here (no longer next to the exe since v0.1.98)")}\n{Paths.Data}（{DataRootKindText()}）",
            T("打开目录", "Open folder"),
            OpenDataDirectory));

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

    /// <summary>
    /// v0.2.0：在资源管理器中打开用户数据目录（= <see cref="Paths.Data"/>，v0.1.98 起默认在 <c>%AppData%\Neko</c>）。
    /// 打开失败只提示、不抛（设置页不该因为打不开一个文件夹而崩）。
    /// </summary>
    private void OpenDataDirectory()
    {
        try
        {
            Paths.EnsureCreated();   // 目录可能还没被建出来（例如刚装还没跑过主流程），先兜底建
            Process.Start(new ProcessStartInfo { FileName = Paths.Data, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开数据目录失败：{ex.Message}\n\n{Paths.Data}", "Neko",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>数据根来源的人话说明——让用户明白"为什么在这里"（见 <see cref="Paths.DataKind"/>）。</summary>
    private static string DataRootKindText() => Paths.DataKind switch
    {
        DataRootKind.EnvOverride => "由环境变量 NEKO_DATA_DIR 指定",
        DataRootKind.Portable => @"便携模式：程序根 data\",
        DataRootKind.ExeDirFallback => @"兜底：程序根 data\",
        _ => "默认位置（%AppData%）",
    };
}
