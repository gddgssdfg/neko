using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 命理助手设置页（P0）：出生日期/时间/性别/历法录入 + <b>实时四柱预览</b> + API Key + 播报设置。
/// 全部经 <see cref="IPluginStorage"/> 持久化。控件统一走 SDK 的 <see cref="PluginUi"/> 深色工厂。
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

        // ---- API Key（P3 使用） ----
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
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(keyBox);

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

        // ---- 保存 ----
        var saveBtn = PluginUi.Button("保存");
        saveBtn.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(saveBtn);

        // ==== 实时预览：任一输入变化即重算四柱 ====
        void RefreshPreview()
        {
            try
            {
                var draft = ReadInputs(
                    p, calCombo, leapCheck, yearBox, monthBox, dayBox,
                    hourBox, minuteBox, genderCombo, timeText,
                    keyBox, broadcastCheck, applyApiAndBroadcast: false);

                if (!draft.HasBirth)
                {
                    preview.Text = "请填写完整的年 / 月 / 日，将实时显示四柱。";
                    return;
                }

                var chart = BaziEngine.ComputeProfile(draft);
                var timePart = chart.HasTime ? chart.TimePillar : "—";
                preview.Text =
                    $"四柱：{chart.YearPillar} {chart.MonthPillar} {chart.DayPillar} {timePart}\n" +
                    $"日主：{chart.DayMaster}（{chart.DayWuXing}）    星座：{chart.XingZuo}\n" +
                    $"纳音：{chart.DayNaYin}\n" +
                    $"起运：{chart.YunStartText}";
            }
            catch
            {
                preview.Text = "输入有误，请检查年 / 月 / 日 / 时 / 分。";
            }
        }

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

        // 保存：把输入写回档案并持久化
        saveBtn.Click += (_, _) =>
        {
            var draft = ReadInputs(
                p, calCombo, leapCheck, yearBox, monthBox, dayBox,
                hourBox, minuteBox, genderCombo, timeText,
                keyBox, broadcastCheck, applyApiAndBroadcast: true);
            draft.Save(_storage);
            RefreshPreview();
            MessageBox.Show("已保存", "命理助手设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        RefreshPreview();
        return root;
    }

    /// <summary>把当前输入组装成一份档案（不落盘）。<paramref name="applyApiAndBroadcast"/> 为 true 时并入 Key 与播报设置。</summary>
    private static BaziProfile ReadInputs(
        BaziProfile baseProfile, ComboBox calCombo, CheckBox leapCheck,
        TextBox yearBox, TextBox monthBox, TextBox dayBox,
        TextBox hourBox, TextBox minuteBox, ComboBox genderCombo,
        TextBox timeText, PasswordBox keyBox, CheckBox broadcastCheck,
        bool applyApiAndBroadcast)
    {
        var draft = new BaziProfile
        {
            Year = ParseInt(yearBox.Text),
            Month = ParseInt(monthBox.Text),
            Day = ParseInt(dayBox.Text),
            Hour = ParseNullableInt(hourBox.Text),
            Minute = ParseNullableInt(minuteBox.Text),
            IsLunar = calCombo.SelectedIndex == 1,
            LeapMonth = leapCheck.IsChecked == true,
            Gender = genderCombo.SelectedIndex == 1 ? 1 : 0,
            // 未在 P0 UI 编辑的字段沿用原档案
            Mbti = baseProfile.Mbti,
        };

        if (applyApiAndBroadcast)
        {
            draft.ApiKey = string.IsNullOrWhiteSpace(keyBox.Password) ? null : keyBox.Password;
            draft.DailyBroadcastEnabled = broadcastCheck.IsChecked == true;
            draft.BroadcastTime = string.IsNullOrWhiteSpace(timeText.Text) ? "08:00" : timeText.Text.Trim();
        }
        else
        {
            // 预览阶段保留原值，避免误清空
            draft.ApiKey = baseProfile.ApiKey;
            draft.DailyBroadcastEnabled = baseProfile.DailyBroadcastEnabled;
            draft.BroadcastTime = baseProfile.BroadcastTime;
        }

        return draft;
    }

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
