// v0.1.84：从 SettingsWindow.cs 按功能区域拆出（审计 P2-11）——卡片 / 控件构建器
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
    // ============================ 卡片构建器 ============================

    private Border BuildToggleCard(string title, string? desc, bool initial, Action<bool> onChange)
    {
        var sw = new ToggleSwitch(ToggleOff, Accent) { IsOn = initial };   // v0.1.50：主题色
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
        var unselectedBg = BgCardHover;

        void RefreshSelection(int newIdx)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var brush = (SolidColorBrush)buttons[i].Background;
                ((TextBlock)buttons[i].Child).Foreground = new SolidColorBrush(newIdx == i ? Colors.White : TextMain);
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
                MinHeight = 30,   // v0.1.82：所有选项按钮统一高度，避免各卡看起来参差
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = options[i], Foreground = new SolidColorBrush(i == selectedIdx ? Colors.White : TextMain), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
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
        var unselectedBg = BgCardHover;

        void RefreshSelection(int newIdx)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var brush = (SolidColorBrush)buttons[i].Background;
                ((TextBlock)buttons[i].Child).Foreground = new SolidColorBrush(newIdx == i ? Colors.White : TextMain);
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
                MinHeight = 30,   // v0.1.82：所有选项按钮统一高度，避免各卡看起来参差
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = options[i], Foreground = new SolidColorBrush(i == selectedIdx ? Colors.White : TextMain), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            };
            btn.MouseLeftButtonUp += (_, _) =>
            {
                onChange(cap);
                RefreshSelection(cap);
            };
            buttons.Add(btn);
            sp.Children.Add(btn);
        }
        return BuildWideCard(title, desc, sp);
    }

    /// <summary>
    /// v0.1.82：宽卡片——控件放在标题/说明**下面一整行**，宽度撑满卡片。
    /// <para>为什么需要它：<see cref="BuildCard"/> 把控件放进 Grid 的 <c>Auto</c> 列，
    /// 而 WPF 的 Auto 列**用无限宽度测量子元素** → 里面的 <see cref="WrapPanel"/> 永远不换行，
    /// 直接横向撑爆被裁掉。皮肤数量一多就表现为"标签只显示前几个、后面的被切掉"。
    /// 会换行 / 数量可变的控件（皮肤选择器等）必须用这个版本。</para>
    /// </summary>
    private Border BuildWideCard(string title, string? description, FrameworkElement control)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(TextMain),
        });
        if (description != null)
            sp.Children.Add(new TextBlock
            {
                Text = description, FontSize = 11.5, Foreground = new SolidColorBrush(TextSub),
                TextWrapping = TextWrapping.Wrap,
            });
        control.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(control);
        return new Border
        {
            Background = new SolidColorBrush(BgCard),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(0, 0, 0, 14),
            Child = sp,
        };
    }

    /// <summary>
    /// v0.1.81：构建「+ / −」步进卡片，并返回可刷新句柄 <paramref name="refresh"/>。
    /// <para>场景：切换皮肤可能自动套用 recommendedScale 从而改变 <c>Config.Scale</c>，
    /// 但卡片文案是构建时算好的——必须能外部刷新，否则设置页显示值与真实值不一致。</para>
    /// <para>传入 <paramref name="readCurrent"/> 时：refresh 从它重读真实值刷新文案；
    /// 且 ± 每次点击也以它为基准起算（见 <see cref="MakeStepperButtons"/>），
    /// 保证外部改过数值后按 ± 不会从构建时的旧值漂移。</para>
    /// </summary>
    private Border BuildStepperCard(string title, string? desc, double initial, double min, double max, double step,
        Action<double> onChange, Func<double, string> formatter, Func<double>? readCurrent, out Action refresh)
    {
        var valueText = new TextBlock
        {
            Text = formatter(initial),
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(AccentText),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(valueText);
        double value = initial;
        var (decBtn, incBtn) = MakeStepperButtons(initial, step, v =>
        {
            var clamped = Math.Clamp(v, min, max);
            value = clamped;
            valueText.Text = formatter(clamped);
            onChange(clamped);
        }, readCurrent);
        sp.Children.Add(decBtn);
        sp.Children.Add(incBtn);
        refresh = () =>
        {
            if (readCurrent is not null) value = Math.Clamp(readCurrent(), min, max);
            valueText.Text = formatter(value);
        };
        return BuildCard(title, desc, sp);
    }

    /// <summary>无需外部刷新的步进卡片（透明度 / 径向菜单数量等）。</summary>
    private Border BuildStepperCard(string title, string? desc, double initial, double min, double max, double step,
        Action<double> onChange, Func<double, string> formatter)
        => BuildStepperCard(title, desc, initial, min, max, step, onChange, formatter, null, out _);

    private (Border, Border) MakeStepperButtons(double initial, double step, Action<double> onChange, Func<double>? readCurrent = null)
    {
        // v0.1.82：± 每次从「活的当前值」起算（readCurrent），而不是构建时的常量——
        // 否则外部改动数值后（如切皮肤自动套用推荐缩放）会从旧基准漂移。
        // readCurrent 为 null 时退化为内部自持值（无外部写入者的卡片，行为与旧版一致）。
        double value = initial;
        double Base() => readCurrent is null ? value : readCurrent();
        var dec = MakeStepperButton("−", () => { value = Base() - step; onChange(value); });
        var inc = MakeStepperButton("+", () => { value = Base() + step; onChange(value); });
        return (dec, inc);
    }

    private Border MakeStepperButton(string label, Action onClick)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(BgCardHover),
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
            Background = new SolidColorBrush(BgCardHover),
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
            Background = new SolidColorBrush(BgCardHover),
            BorderBrush = new SolidColorBrush(BorderC),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = T("检查更新", "Check for updates"), Foreground = new SolidColorBrush(TextMain), FontSize = 13 },
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
                    // v0.2.2：失败提示要说实话并**可操作**。真实原因有两类，且此前都显示成"请稍后重试"：
                    //   ① 网络不可达（暂时性，重试有意义）；
                    //   ② 发布仓库对未登录者不可见（配置性，**重试一万次也没用**）。
                    // 再给一个「打开仓库页」的出口，免得用户对着一个永远不会成功的按钮反复点。
                    var r2 = MessageBox.Show(
                        T("检查失败：网络不可用，或发布仓库当前不可见（更新检查只支持公开仓库）。\n\n是否打开仓库页手动查看？",
                          "Check failed: no network, or the release repository isn't publicly visible (the updater only supports public repositories).\n\nOpen the repository page?"),
                        "Neko", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r2 == MessageBoxResult.Yes) OpenUrl(UpdateService.RepoUrl);
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
                label.Text = T("检查更新", "Check for updates");
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
            Background = new SolidColorBrush(BgRoot),
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

    /// <summary>
    /// v0.1.82：外观选择器 —— 一个皮肤一行，左侧是该皮肤的预览缩略图 + 名称/id。
    /// <para>预览图来自 <c>neko-assets/&lt;id&gt;/thumb.png</c>（由 tools/gen_skin_previews.py 生成）；
    /// 缺失时显示占位文字，不影响选择。</para>
    /// </summary>
    private Border BuildSkinPicker(IReadOnlyList<SkinInfo> skins, string currentId, Action<string> onPick)
    {
        var unselected = BgCardHover;
        var list = new StackPanel();
        var rows = new List<(Border Row, SolidColorBrush Brush)>();

        void Refresh(string picked)
        {
            foreach (var (row, brush) in rows)
            {
                bool sel = (row.Tag as string) == picked;
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(sel ? BgSideSel : unselected, TimeSpan.FromMilliseconds(150)));
            }
        }

        foreach (var skin in skins)
        {
            bool isCur = string.Equals(skin.Id, currentId, StringComparison.OrdinalIgnoreCase);

            var thumb = new Border
            {
                Width = 48, Height = 48,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(BgRoot),
                BorderBrush = new SolidColorBrush(BorderC),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var preview = LoadSkinPreview(skin);
            thumb.Child = preview is not null
                ? new Image { Source = preview, Stretch = Stretch.Uniform, Margin = new Thickness(3) }
                : (FrameworkElement)new TextBlock
                {
                    Text = "无预览", FontSize = 9,
                    Foreground = new SolidColorBrush(TextSub),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            textCol.Children.Add(new TextBlock
            {
                Text = skin.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextMain),
            });
            textCol.Children.Add(new TextBlock
            {
                Text = skin.Id, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(TextSub),
            });

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(thumb);
            line.Children.Add(textCol);

            var row = new Border
            {
                Child = line,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(BorderC),
                Cursor = Cursors.Hand,
                Tag = skin.Id,
                Background = new SolidColorBrush(isCur ? BgSideSel : unselected),
            };
            row.MouseLeftButtonUp += (_, _) => { onPick(skin.Id); Refresh(skin.Id); };
            rows.Add((row, (SolidColorBrush)row.Background));
            list.Children.Add(row);
        }

        return BuildWideCard(T("外观风格", "Choose a companion"),
            T($"挑一只喜欢的猫咪陪你。当前有 {skins.Count} 套外观。", $"Find your favorite companion. {skins.Count} styles available."),
            list);
    }

    // ---- v0.1.88：皮肤缩略图内存缓存 ----
    // 每次切回 pet 页都要为每套皮肤重读 thumb.png（FileStream + BitmapImage 解码）——
    // 缓存 Frozen 的 BitmapSource（可跨窗口安全共享），来回切页不再重复解码。
    // key = 绝对路径 + 文件 mtime：皮肤素材被改写（mtime 变）自动失效；删掉的旧项自然不再命中。
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _thumbCache = new();
    private const int ThumbCacheLimit = 64;   // 8 套皮肤 × 若干版本，足够；超限则整体清空（简化 LRU）

    /// <summary>读 neko-assets/&lt;id&gt;/thumb.png（不能叫 preview.png：csproj 的 Remove 规则会排除 *preview*）。用 FileStream+BitmapImage(OnLoad) 避免锁文件 + 避免 PngBitmapDecoder 偶发黑帧。</summary>
    private static System.Windows.Media.Imaging.BitmapSource? LoadSkinPreview(SkinInfo skin)
    {
        try
        {
            var path = System.IO.Path.Combine(skin.Directory, "thumb.png");
            if (!System.IO.File.Exists(path)) return null;

            // v0.1.88：先查缓存（key 含 mtime → 素材改动即失效）
            long mtime = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            string key = path + "|" + mtime;
            if (_thumbCache.TryGetValue(key, out var hit)) return hit;

            using var fs = System.IO.File.OpenRead(path);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();

            if (_thumbCache.Count >= ThumbCacheLimit) _thumbCache.Clear();   // 简化 LRU：超限整体清空
            _thumbCache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private Border BuildCard(string title, string? description, FrameworkElement control)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextMain) });
        if (description != null)
            sp.Children.Add(new TextBlock { Text = description, FontSize = 11.5, Foreground = new SolidColorBrush(TextSub), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        // v0.1.82：控件必须垂直居中，不能让它被 Grid 行高拉伸。
        // 坑：Grid 子元素默认 VerticalAlignment=Stretch，行高 = max(左侧文字, 右侧控件)，
        // 于是"文字换行变高"会把右侧的选项按钮一起拉成方块，卡片下方留出大片空白（灯灯截图报的那个）。
        control.VerticalAlignment = VerticalAlignment.Center;
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
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 16, 20, 16),   // v0.1.38：内边距加大，文字不再贴卡边
            Margin = new Thickness(0, 0, 0, 14),       // v0.1.38：卡片间距 8→14
            Child = grid,
        };
    }

    // ============================ ToggleSwitch 控件 ============================

    /// <summary>v0.1.31 自定义深色 toggle 开关（40x22 胶囊 + 18px 圆点 + 0.15s 滑动动画）；v0.1.50 支持主题色（offColor 参数）</summary>
    private sealed class ToggleSwitch : Border
    {
        public event Action<bool>? Toggled;
        private bool _isOn;
        private readonly Ellipse _knob;
        private readonly Brush _onColor;
        private readonly Brush _offColor;

        public bool IsOn
        {
            get => _isOn;
            set { if (_isOn == value) return; _isOn = value; UpdateVisual(); }
        }

        public ToggleSwitch(Color offColor, Color onColor)
        {
            _offColor = new SolidColorBrush(offColor);
            _onColor = new SolidColorBrush(onColor);
            Focusable = true;
            KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { IsOn = !_isOn; Toggled?.Invoke(_isOn); e.Handled = true; } };
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
            Background = _isOn ? _onColor : _offColor;
            // 圆点滑到另一侧（动画 0.15s）
            var anim = new DoubleAnimation(_isOn ? 18 : 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            ((TranslateTransform)_knob.RenderTransform!).BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }
}
