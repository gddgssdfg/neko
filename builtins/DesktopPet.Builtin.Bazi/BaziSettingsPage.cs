using System.Windows;
using System.Windows.Controls;
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

    public BaziSettingsPage(IPluginStorage storage) => _storage = storage;

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

        // ---- 人格测试（P4） ----
        root.Children.Add(PluginUi.SectionHeader("人格测试（生日性格分析）"));

        var mbtiStatus = PluginUi.Text(MbtiStatusText(currentMbti));
        mbtiStatus.FontSize = 12.5;
        mbtiStatus.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(mbtiStatus);

        var quizHint = PluginUi.Text($"共 {MbtiQuiz.Questions.Count} 题，每题二选一；答完点「计算并保存」。可随时重测覆盖。", sub: true);
        quizHint.FontSize = 11;
        quizHint.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(quizHint);

        var answerRows = new List<(RadioButton A, RadioButton B)>();
        var index = 0;
        foreach (var q in MbtiQuiz.Questions)
        {
            index++;
            root.Children.Add(QuizQuestionBlock(index, q, answerRows));
        }

        var quizBtn = PluginUi.Button("计算并保存");
        quizBtn.HorizontalAlignment = HorizontalAlignment.Left;
        quizBtn.Margin = new Thickness(0, 6, 0, 12);
        root.Children.Add(quizBtn);

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
            Mbti = currentMbti,
            ApiKey = string.IsNullOrWhiteSpace(keyBox.Password) ? null : keyBox.Password,
            DailyBroadcastEnabled = broadcastCheck.IsChecked == true,
            BroadcastTime = string.IsNullOrWhiteSpace(timeText.Text) ? "08:00" : timeText.Text.Trim(),
        };

        void Persist()
        {
            var draft = FullDraft();
            draft.Save(_storage);
            RefreshPreview();
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

        // ==== 人格测试提交：计分 → 覆盖当前 MBTI → 落盘 ====
        void SubmitQuiz()
        {
            var answers = answerRows
                .Select(r => r.A.IsChecked == true ? 0 : r.B.IsChecked == true ? 1 : -1)
                .ToList();
            var type = MbtiQuiz.Score(answers);
            currentMbti = type;
            Persist();
            mbtiStatus.Text = MbtiStatusText(currentMbti);
            MessageBox.Show($"你的类型：{type}\n{MbtiQuiz.Describe(type)}", "人格测试",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==== 事件接线 ====
        yearBox.TextChanged += (_, _) => RefreshPreview();
        monthBox.TextChanged += (_, _) => RefreshPreview();
        dayBox.TextChanged += (_, _) => RefreshPreview();
        hourBox.TextChanged += (_, _) => RefreshPreview();
        minuteBox.TextChanged += (_, _) => RefreshPreview();
        calCombo.SelectionChanged += (_, _) =>
        {
            leapCheck.IsEnabled = calLunar.IsSelected;
            RefreshPreview();
        };
        leapCheck.Checked += (_, _) => RefreshPreview();
        leapCheck.Unchecked += (_, _) => RefreshPreview();
        genderCombo.SelectionChanged += (_, _) => RefreshPreview();

        quizBtn.Click += (_, _) => SubmitQuiz();
        saveBtn.Click += (_, _) =>
        {
            Persist();
            MessageBox.Show("已保存", "命理助手设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        RefreshPreview();

        // 内容较长：整体可滚动，避免撑爆宿主设置窗口
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Content = root,
        };
    }

    /// <summary>MBTI 状态文案。</summary>
    private static string MbtiStatusText(string? mbti)
        => string.IsNullOrWhiteSpace(mbti)
            ? "当前类型：尚未测试（答完下面题目点「计算并保存」）"
            : $"当前类型：{mbti}　{MbtiQuiz.Describe(mbti)}（可重测覆盖）";

    /// <summary>一道题：题干 + 两个互斥选项（RadioButton，同题共用 GroupName）。</summary>
    private static StackPanel QuizQuestionBlock(int index, MbtiQuestion q, List<(RadioButton A, RadioButton B)> collector)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var title = PluginUi.Text($"{index}. {q.Text}");
        title.FontSize = 12.5;
        title.Margin = new Thickness(0, 0, 0, 2);
        block.Children.Add(title);

        var group = "mbti_" + q.Id;
        var optA = RadioOption(q.OptionA, group);
        var optB = RadioOption(q.OptionB, group);
        collector.Add((optA, optB));

        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        opts.Children.Add(optA);
        opts.Children.Add(optB);
        block.Children.Add(opts);

        return block;
    }

    private static RadioButton RadioOption(string text, string group) => new()
    {
        Content = text,
        GroupName = group,
        Foreground = PluginUi.TextMain,
        Margin = new Thickness(0, 0, 18, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

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
