using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// 人格测试独立窗口（v0.1.73，纯 C# 构建，与 ChatWindow 一致，不引入 XAML）。
/// <para>连续 93 题 + 顶部进度条 + 当前维度标签；每题二选一 RadioButton，可回退上一题改答案。</para>
/// <para>答完全部点击「提交」→ <see cref="MbtiQuiz.Score"/> 计分 → 回调 <see cref="OnCompleted"/>。
/// 中途关闭不提交、不改变档案。</para>
/// </summary>
public sealed class MbtiQuizWindow : Window
{
    private readonly Action<string> _onCompleted;
    private readonly int?[] _answers;
    private readonly TextBlock _dimensionLabel;
    private readonly TextBlock _questionText;
    private readonly RadioButton _optA;
    private readonly RadioButton _optB;
    private readonly Button _prevBtn;
    private readonly Button _nextBtn;
    private readonly ProgressBar _progress;
    private int _index;

    public MbtiQuizWindow(Action<string> onCompleted)
    {
        _onCompleted = onCompleted;
        _answers = new int?[MbtiQuiz.Questions.Count];

        Title = "Neko 人格测试";
        Width = 520;
        Height = 600;
        MinWidth = 380;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = NekoPalette.Brush(NekoDesign.Current.Background);
        NekoDesign.Apply(this);
        Opacity = 0;

        // ---- 标题 + 进度条 ----
        var title = new TextBlock
        {
            Text = "人格测试（生日性格分析）",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
            Margin = new Thickness(20, 16, 20, 0),
        };
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = MbtiQuiz.Questions.Count,
            Height = 6,
            Margin = new Thickness(20, 10, 20, 0),
            Foreground = NekoPalette.Brush(NekoDesign.Current.Accent),
            Background = NekoPalette.Brush(NekoDesign.Current.Surface),
        };
        _dimensionLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = NekoPalette.Brush(NekoDesign.Current.Muted),
            Margin = new Thickness(20, 8, 20, 0),
        };

        // ---- 题目区 ----
        _questionText = new TextBlock
        {
            FontSize = 14,
            Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 12),
            MinHeight = 60,
        };

        _optA = new RadioButton
        {
            Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
            FontSize = 13,
            Margin = new Thickness(20, 4, 20, 4),
            GroupName = "mbti",
            Content = "",
        };
        _optB = new RadioButton
        {
            Foreground = NekoPalette.Brush(NekoDesign.Current.Text),
            FontSize = 13,
            Margin = new Thickness(20, 4, 20, 4),
            GroupName = "mbti",
            Content = "",
        };

        // ---- 底部导航 ----
        _prevBtn = PluginUi.Button("上一题");
        _prevBtn.Click += (_, _) => Move(-1);
        _nextBtn = PluginUi.Button("下一题");
        _nextBtn.Click += (_, _) => Move(1);

        var nav = new Grid { Margin = new Thickness(20, 16, 20, 20) };
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_prevBtn, 0);
        Grid.SetColumn(_nextBtn, 1);
        _prevBtn.Margin = new Thickness(0, 0, 6, 0);
        _nextBtn.Margin = new Thickness(6, 0, 0, 0);
        nav.Children.Add(_prevBtn);
        nav.Children.Add(_nextBtn);

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(_progress);
        stack.Children.Add(_dimensionLabel);
        stack.Children.Add(_questionText);
        stack.Children.Add(_optA);
        stack.Children.Add(_optB);
        stack.Children.Add(nav);

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // 选项点击即记录答案，无需额外按钮
        _optA.Checked += (_, _) => { if (_index < _answers.Length) _answers[_index] = 0; };
        _optB.Checked += (_, _) => { if (_index < _answers.Length) _answers[_index] = 1; };

        Loaded += (_, _) =>
        {
            ShowQuestion(0);
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        };
    }

    private void ShowQuestion(int index)
    {
        _index = index;
        var q = MbtiQuiz.Questions[index];
        _questionText.Text = q.Text;
        _dimensionLabel.Text = $"{DimensionName(q.Dimension)} · 第 {index + 1}/{MbtiQuiz.Questions.Count} 题";
        _progress.Value = index;

        // 回填已选答案（先解绑避免触发 Checked 覆盖）
        _optA.IsChecked = false;
        _optB.IsChecked = false;
        var saved = _answers[index];
        if (saved == 0) _optA.IsChecked = true;
        else if (saved == 1) _optB.IsChecked = true;

        // 选项文案
        _optA.Content = q.OptionA;
        _optB.Content = q.OptionB;

        // 导航按钮状态
        _prevBtn.IsEnabled = index > 0;
        bool last = index == MbtiQuiz.Questions.Count - 1;
        _nextBtn.Content = last ? "提交" : "下一题";
        _nextBtn.IsEnabled = true;
    }

    private void Move(int delta)
    {
        int next = _index + delta;
        if (next < 0) return;

        // 最后一题点「提交」
        if (_index == MbtiQuiz.Questions.Count - 1 && delta > 0)
        {
            Submit();
            return;
        }

        if (next >= MbtiQuiz.Questions.Count) return;
        ShowQuestion(next);
    }

    private void Submit()
    {
        var answers = _answers.Select(a => a ?? -1).ToList();
        var type = MbtiQuiz.Score(answers);
        var desc = MbtiQuiz.Describe(type);
        MessageBox.Show($"你的类型：{type}　{desc}\n\n可在设置页保存后用于每日播报的个性建议。",
            "人格测试", MessageBoxButton.OK, MessageBoxImage.Information);
        _onCompleted(type);
        Close();
    }

    private static string DimensionName(string dim) => dim switch
    {
        "EI" => "能量来源",
        "SN" => "信息获取",
        "TF" => "决策方式",
        "JP" => "生活组织",
        _ => dim,
    };
}
