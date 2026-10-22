using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 命理助手设置页：出生信息 + <b>实时四柱预览</b> + API Key + 每日播报 + <b>人格测试（P4）</b>。
/// 全部经 <see cref="IPluginStorage"/> 持久化。控件统一走 SDK 的 <see cref="PluginUi"/> 深色工厂；
/// 内容较长，整体包在 <see cref="ScrollViewer"/> 里，避免撑爆宿主设置窗口。
/// <para><see cref="Title"/> 必须等于 <c>Manifest.Id</c>，否则宿主设置窗口匹配不上（手册 5.4）。</para>
/// </summary>
public sealed class BaziSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;

    /// <summary>
    /// v0.1.84：保存后的回调（由插件注入），用于让插件按新的播报设置<b>重排每日播报</b>。
    /// 可空 —— 便于测试或在不关心重排的场景下构造。
    /// </summary>
    private readonly Action? _onSaved;

    /// <summary>
    /// v0.1.82：出生字段预览的输入去抖计时器（300ms）。
    /// <para>避免逐键触发同步排盘（<see cref="BaziEngine.ComputeProfile"/> 是同步天文 / 历法计算）导致输入卡顿。
    /// 在 <see cref="BuildContent"/> 内创建，内容卸载（<c>Unloaded</c>）时停止并释放。</para>
    /// </summary>
    private DispatcherTimer? _previewDebounce;

    /// <param name="storage">插件私有存储。</param>
    /// <param name="onSaved">
    /// 可选：保存成功后回调（在 UI 线程上执行）。插件用它重排每日播报 ——
    /// 用户改了播报时间 / 开关后，旧排期必须被取消、按新值重排，否则要么按旧时刻触发、要么不触发。
    /// </param>
    public BaziSettingsPage(IPluginStorage storage, Action? onSaved = null)
    {
        _storage = storage;
        _onSaved = onSaved;
    }

    public string Id => "com.lantern.bazi";
    public string Title => "com.lantern.bazi";

    public object BuildContent()
    {
        var p = BaziProfile.Load(_storage);

        // 页面内「当前 MBTI」的权威值：人格测试提交与主「保存」共用它，避免互相覆盖。
        var currentMbti = p.Mbti;

        var root = new StackPanel { Margin = new Thickness(10) };

        // ---- 出生信息 ----
        root.Children.Add(PluginUi.SectionHeader("出生信息"));

        // 历法 + 闰月
        var calCombo = new ComboBox
        {
            Width = 90,
            Foreground = PluginUi.TextMain,
            Background = PluginUi.CardBg,
        };
        var calSolar = new ComboBoxItem { Content = "公历" };
        var calLunar = new ComboBoxItem { Content = "农历" };
        calCombo.Items.Add(calSolar);
        calCombo.Items.Add(calLunar);
        (p.IsLunar ? calLunar : calSolar).IsSelected = true;

        var leapCheck = new CheckBox
        {
            Content = "闰月",
            IsChecked = p.LeapMonth,
            IsEnabled = p.IsLunar,
            Foreground = PluginUi.TextMain,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var calRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        calRow.Children.Add(Label("历法"));
        calRow.Children.Add(calCombo);
        calRow.Children.Add(leapCheck);
        root.Children.Add(calRow);

        // 年 / 月 / 日
        var yearBox = NumBox(p.Year > 0 ? p.Year.ToString() : "", 72);
        var monthBox = NumBox(p.Month > 0 ? p.Month.ToString() : "", 48);
        var dayBox = NumBox(p.Day > 0 ? p.Day.ToString() : "", 48);

        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        dateRow.Children.Add(Label("年"));
        dateRow.Children.Add(yearBox);
        dateRow.Children.Add(Label("月"));
        dateRow.Children.Add(monthBox);
        dateRow.Children.Add(Label("日"));
        dateRow.Children.Add(dayBox);
        root.Children.Add(dateRow);

        // 时 / 分（可留空 → 不计时柱）
        var hourBox = NumBox(p.Hour is { } h ? h.ToString() : "", 48);
        var minuteBox = NumBox(p.Minute is { } m ? m.ToString() : "", 48);

        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        timeRow.Children.Add(Label("时"));
        timeRow.Children.Add(hourBox);
        timeRow.Children.Add(Label("分"));
        timeRow.Children.Add(minuteBox);
        root.Children.Add(timeRow);

        var timeHint = PluginUi.Text("时辰可留空（留空则不计时柱）。按标准时间排盘，暂不处理真太阳时。", sub: true);
        timeHint.FontSize = 11;
        timeHint.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(timeHint);

        // 性别
        var genderCombo = new ComboBox
        {
            Width = 90,
            Foreground = PluginUi.TextMain,
            Background = PluginUi.CardBg,
        };
        var genderFemale = new ComboBoxItem { Content = "女" };
        var genderMale = new ComboBoxItem { Content = "男" };
        genderCombo.Items.Add(genderFemale);
        genderCombo.Items.Add(genderMale);
        (p.Gender == 1 ? genderMale : genderFemale).IsSelected = true;

        var genderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        genderRow.Children.Add(Label("性别"));
        genderRow.Children.Add(genderCombo);
        root.Children.Add(genderRow);

        // ---- 实时命盘预览 ----
        root.Children.Add(PluginUi.SectionHeader("实时命盘预览"));

        var preview = PluginUi.Text("");
        preview.FontSize = 13;
        preview.LineHeight = 20;
        preview.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(preview);

        // ---- API Key（P3 AI 咨询用） ----
        root.Children.Add(PluginUi.SectionHeader("AI 咨询（可选）"));

        var keyBox = new PasswordBox
        {
            Password = p.ApiKey ?? "",
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = PluginUi.TextMain,
            Background = PluginUi.CardBg,
            BorderBrush = PluginUi.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 0, 4),
        };
        root.Children.Add(keyBox);

        var keyHint = PluginUi.Text("DeepSeek API Key：仅保存在本机（DPAPI 加密），不会上传到任何服务器。", sub: true);
        keyHint.FontSize = 11;
        keyHint.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(keyHint);

        // ---- 每日播报 ----
        root.Children.Add(PluginUi.SectionHeader("每日播报"));

        var broadcastCheck = new CheckBox
        {
            Content = "开启每日运势播报",
            IsChecked = p.DailyBroadcastEnabled,
            Foreground = PluginUi.TextMain,
            Margin = new Thickness(0, 0, 0, 8),
        };
        root.Children.Add(broadcastCheck);

        var timeText = PluginUi.Input(p.BroadcastTime);
        timeText.Width = 72;
        var broadcastRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        broadcastRow.Children.Add(Label("时刻"));
        broadcastRow.Children.Add(timeText);
        var fmtHint = PluginUi.Text("  （格式 HH:mm）", sub: true);
        fmtHint.FontSize = 11;
        broadcastRow.Children.Add(fmtHint);
        root.Children.Add(broadcastRow);

        // ---- 人格定制（v0.1.73：默认关闭，开启后弹窗做题或直接选型） ----
        root.Children.Add(PluginUi.SectionHeader("人格定制（生日性格分析）"));

        // 开关：默认关闭
        var mbtiCheck = new CheckBox
        {
            Content = "启用人格定制",
            IsChecked = p.MbtiEnabled,
            Foreground = PluginUi.TextMain,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        root.Children.Add(mbtiCheck);

        var mbtiStatus = PluginUi.Text(MbtiStatusText(p.MbtiEnabled, currentMbti));
        mbtiStatus.FontSize = 12.5;
        mbtiStatus.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(mbtiStatus);

        // 直接选择下拉（16 型）—— 默认关闭时隐藏，开启后才显示
        var typeCombo = new ComboBox
        {
            Width = 220,
            Foreground = PluginUi.TextMain,
            Background = PluginUi.CardBg,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = p.MbtiEnabled ? Visibility.Visible : Visibility.Collapsed,
        };
        foreach (var t in MbtiQuiz.AllTypes)
            typeCombo.Items.Add(new ComboBoxItem { Content = $"{t}　{MbtiQuiz.Describe(t)}", Tag = t });
        typeCombo.SelectionChanged += (_, _) =>
        {
            if (typeCombo.SelectedItem is ComboBoxItem { Tag: string t })
            {
                currentMbti = t;
                mbtiStatus.Text = MbtiStatusText(mbtiCheck.IsChecked == true, currentMbti);
            }
        };
        root.Children.Add(typeCombo);

        // 开始测试按钮（弹独立窗口）—— 默认关闭时隐藏，开启后才显示
        var quizBtn = PluginUi.Button("开始测试");
        quizBtn.HorizontalAlignment = HorizontalAlignment.Left;
        quizBtn.Margin = new Thickness(0, 0, 0, 4);
        quizBtn.Visibility = p.MbtiEnabled ? Visibility.Visible : Visibility.Collapsed;
        quizBtn.Click += (_, _) =>
        {
            new MbtiQuizWindow(type =>
            {
                currentMbti = type;
                mbtiStatus.Text = MbtiStatusText(mbtiCheck.IsChecked == true, currentMbti);
            }).Show();
        };
        root.Children.Add(quizBtn);

        var quizHint = PluginUi.Text($"共 {MbtiQuiz.Questions.Count} 题，或直接在上方下拉选择你的类型。关闭开关则播报走通用建议。", sub: true);
        quizHint.FontSize = 11;
        quizHint.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(quizHint);

        // 开关联动：开启/关闭时刷新状态 + 入口显隐；关闭清空类型
        mbtiCheck.Checked += (_, _) =>
        {
            mbtiStatus.Text = MbtiStatusText(true, currentMbti);
            quizBtn.Visibility = Visibility.Visible;
            typeCombo.Visibility = Visibility.Visible;
        };
        mbtiCheck.Unchecked += (_, _) =>
        {
            currentMbti = null;
            typeCombo.SelectedItem = null;
            mbtiStatus.Text = MbtiStatusText(false, null);
            quizBtn.Visibility = Visibility.Collapsed;
            typeCombo.Visibility = Visibility.Collapsed;
        };

        // ---- 保存 ----
        var saveBtn = PluginUi.Button("保存");
        saveBtn.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(saveBtn);

        // ==== 草稿组装 ====
        // 预览只需出生字段（ComputeProfile 只读它们）
        BaziProfile BirthDraft() => new()
        {
            Year = ParseInt(yearBox.Text),
            Month = ParseInt(monthBox.Text),
            Day = ParseInt(dayBox.Text),
            Hour = ParseNullableInt(hourBox.Text),
            Minute = ParseNullableInt(minuteBox.Text),
            IsLunar = calCombo.SelectedIndex == 1,
            LeapMonth = leapCheck.IsChecked == true,
            Gender = genderCombo.SelectedIndex == 1 ? 1 : 0,
        };

        // 落盘：出生 + MBTI + Key + 播报，一次性写全
        BaziProfile FullDraft() => new()
        {
            Year = ParseInt(yearBox.Text),
            Month = ParseInt(monthBox.Text),
            Day = ParseInt(dayBox.Text),
            Hour = ParseNullableInt(hourBox.Text),
            Minute = ParseNullableInt(minuteBox.Text),
            IsLunar = calCombo.SelectedIndex == 1,
            LeapMonth = leapCheck.IsChecked == true,
            Gender = genderCombo.SelectedIndex == 1 ? 1 : 0,
            MbtiEnabled = mbtiCheck.IsChecked == true,
            Mbti = mbtiCheck.IsChecked == true ? currentMbti : null,   // v0.1.73：关闭时强制 null，杜绝残留
            ApiKey = string.IsNullOrWhiteSpace(keyBox.Password) ? null : keyBox.Password,
            DailyBroadcastEnabled = broadcastCheck.IsChecked == true,
            BroadcastTime = string.IsNullOrWhiteSpace(timeText.Text) ? "08:00" : timeText.Text.Trim(),
        };

        void Persist()
        {
            var draft = FullDraft();
            try { draft.Save(_storage); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                MessageBox.Show("Windows 用户加密暂不可用，设置未保存。原有数据保持不变，请检查当前 Windows 用户环境后重试。",
                    "Neko", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshPreview();
            // v0.1.84：保存后通知插件重排每日播报（改时间 / 开关后立即取消旧排期、按新值重排）。
            _onSaved?.Invoke();
        }

        // ==== 实时预览：任一出生字段变化即重算四柱 ====
        void RefreshPreview()
        {
            try
            {
                var draft = BirthDraft();
                if (!draft.HasBirth)
                {
                    preview.Text = "请填写完整的年 / 月 / 日，将实时显示四柱。";
                    return;
                }

                var chart = BaziEngine.ComputeProfile(draft);
                var timePart = chart.HasTime ? chart.TimePillar : "—";
                // 日主五行取日干的单字五行（chart.DayWuXing 是 lunar 的「干+支」两字，不适合展示）
                var dayMasterElement = BaziEngine.ElementOfGan(chart.DayMaster);
                preview.Text =
                    $"四柱：{chart.YearPillar} {chart.MonthPillar} {chart.DayPillar} {timePart}\n" +
                    $"日主：{chart.DayMaster}（{dayMasterElement}）    星座：{chart.XingZuo}\n" +
                    $"纳音：{chart.DayNaYin}\n" +
                    $"起运：{chart.YunStartText}";
            }
            catch
            {
                preview.Text = "输入有误，请检查年 / 月 / 日 / 时 / 分。";
            }
        }

        // ==== 输入去抖（300ms）====
        // RefreshPreview 里会同步跑 BaziEngine.ComputeProfile（天文/历法计算），逐键触发会卡输入。
        // 用 DispatcherTimer 去抖：每次输入都重置计时器，停手 300ms 后才真正重算一次 ——
        // 保证「最终值一定会被计算并预览」（不会出现停手了却不刷新）。
        // 说明：DispatcherTimer 未实现 IDisposable，Stop() 即会将其从 Dispatcher 队列移除；
        //       卸载时 Stop() + 置空即可解除对闭包（含控件引用）的持有，等价于释放。
        _previewDebounce?.Stop();
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce?.Stop();
            RefreshPreview();
        };

        void ScheduleRefresh()
        {
            _previewDebounce!.Stop();
            _previewDebounce!.Start();
        }

        // ==== 事件接线：任一出生命盘字段变化 → 去抖后重算四柱 ====
        yearBox.TextChanged += (_, _) => ScheduleRefresh();
        monthBox.TextChanged += (_, _) => ScheduleRefresh();
        dayBox.TextChanged += (_, _) => ScheduleRefresh();
        hourBox.TextChanged += (_, _) => ScheduleRefresh();
        minuteBox.TextChanged += (_, _) => ScheduleRefresh();
        calCombo.SelectionChanged += (_, _) =>
        {
            leapCheck.IsEnabled = calLunar.IsSelected;
            ScheduleRefresh();
        };
        leapCheck.Checked += (_, _) => ScheduleRefresh();
        leapCheck.Unchecked += (_, _) => ScheduleRefresh();
        genderCombo.SelectionChanged += (_, _) => ScheduleRefresh();

        saveBtn.Click += (_, _) =>
        {
            Persist();
            MessageBox.Show("已保存", "命理助手设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        RefreshPreview();

        // 内容较长：整体可滚动，避免撑爆宿主设置窗口
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Content = root,
        };
        // 页面关闭 / 卸载时停止并释放去抖计时器（DispatcherTimer 无 IDisposable，详见上方说明）。
        scroller.Unloaded += (_, _) =>
        {
            _previewDebounce?.Stop();
            _previewDebounce = null;
        };
        return scroller;
    }

    /// <summary>MBTI 状态文案。</summary>
    private static string MbtiStatusText(bool enabled, string? mbti)
        => !enabled
            ? "未开启（播报走日主通用建议）"
            : string.IsNullOrWhiteSpace(mbti)
                ? "已开启，但尚未选择类型（点「开始测试」或从上方下拉选择）"
                : $"当前类型：{mbti}　{MbtiQuiz.Describe(mbti)}";

    private static TextBlock Label(string text)
    {
        var tb = PluginUi.Text(text, sub: true);
        tb.Margin = new Thickness(0, 0, 6, 0);
        tb.VerticalAlignment = VerticalAlignment.Center;
        return tb;
    }

    private static TextBox NumBox(string value, double width)
    {
        var tb = PluginUi.Input(value);
        tb.Width = width;
        tb.Margin = new Thickness(0, 0, 10, 0);
        tb.TextAlignment = TextAlignment.Center;
        return tb;
    }

    private static int ParseInt(string text)
        => int.TryParse(text.Trim(), out var v) ? v : 0;

    private static int? ParseNullableInt(string text)
        => string.IsNullOrWhiteSpace(text) ? null : (int.TryParse(text.Trim(), out var v) ? v : null);
}
