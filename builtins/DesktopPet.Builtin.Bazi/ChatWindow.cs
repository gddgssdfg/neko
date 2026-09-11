using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Bazi;

/// <summary>
/// AI 命理咨询窗口（纯 C# 构建，与 NotesWindow / TodoWindow / ClipboardWindow 一致，不引入 XAML）。
/// <para>对话区为 <see cref="ScrollViewer"/> + 气泡列表；底部为输入框 + 发送按钮。</para>
/// <para><b>全程异步</b>：发送走 <c>async void</c> + <c>await</c>，绝不阻塞 UI 线程（规格 4.4）。
/// 网络失败在窗口内以可读红色气泡呈现，不弹异常、不卡死。</para>
/// </summary>
public sealed class ChatWindow : Window
{
    private static readonly Brush UserBubbleBg = Frozen(0x33, 0x44, 0x5C);
    private static readonly Brush ErrorText = Frozen(0xE8, 0x9A, 0x9A);

    private readonly Func<string, Task<ChatResult>> _ask;
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _sendBtn;
    private bool _busy;

    /// <param name="history">已有对话历史（用于初始化气泡），仅 user/assistant。</param>
    /// <param name="ask">一次提问的异步处理委托：传入问题，返回结果（失败走 <see cref="ChatResult.Error"/>）。</param>
    public ChatWindow(IReadOnlyList<ChatMessage> history, Func<string, Task<ChatResult>> ask)
    {
        _ask = ask;

        Title = "Neko 命理咨询";
        Width = 420;
        Height = 520;
        MinWidth = 320;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = PluginUi.Bg;
        Opacity = 0;   // 淡入起点

        // ---- 顶部提示 ----
        var hint = new TextBlock
        {
            Text = "结合你的命盘作答，仅供传统文化趣味参考。",
            FontSize = 11,
            Foreground = PluginUi.TextSub,
            Margin = new Thickness(10, 8, 10, 4),
            TextWrapping = TextWrapping.Wrap,
        };

        // ---- 对话区 ----
        _messages.Margin = new Thickness(10, 4, 10, 6);
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _messages,
        };

        // ---- 底部输入区 ----
        var inputGrid = new Grid { Margin = new Thickness(10) };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _input = PluginUi.Input();
        _input.FontSize = 13;
        _input.MinHeight = 30;
        _input.AcceptsReturn = false;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Send(); }
        };
        Grid.SetColumn(_input, 0);

        _sendBtn = PluginUi.Button("发送");
        _sendBtn.Margin = new Thickness(8, 0, 0, 0);
        _sendBtn.Padding = new Thickness(16, 4, 16, 4);
        _sendBtn.Click += (_, _) => Send();
        Grid.SetColumn(_sendBtn, 1);

        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(_sendBtn);

        var dock = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(inputGrid, Dock.Bottom);
        dock.Children.Add(hint);
        dock.Children.Add(inputGrid);
        dock.Children.Add(_scroll);
        Content = dock;

        // ---- 初始化历史气泡 ----
        if (history is { Count: > 0 })
        {
            foreach (var m in history)
                AppendBubble(m.Content, isUser: m.Role == "user");
        }
        else
        {
            AppendBubble("你好呀～可以问我关于你的八字、五行、今日宜忌的问题，我会结合命盘解读。", isUser: false);
        }

        Loaded += (_, _) =>
        {
            _input.Focus();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        };
    }

    /// <summary>发送当前输入（异步，不阻塞 UI）。空输入 / 请求中忽略。</summary>
    private async void Send()
    {
        var question = _input.Text.Trim();
        if (question.Length == 0 || _busy) return;

        _busy = true;
        _sendBtn.IsEnabled = false;
        _input.Clear();
        AppendBubble(question, isUser: true);

        var pending = AppendBubble("正在解读…", isUser: false);
        try
        {
            var result = await _ask(question);
            SetBubbleText(pending, result.Ok ? result.Text : (result.Error ?? "AI 暂时无法回答，请稍后再试。"), isError: !result.Ok);
        }
        catch (Exception ex)
        {
            // 兜底：委托本身抛异常也不应拖垮窗口
            SetBubbleText(pending, $"请求出错：{ex.Message}", isError: true);
        }
        finally
        {
            _busy = false;
            _sendBtn.IsEnabled = true;
            _input.Focus();
        }
    }

    /// <summary>在对话区追加一条气泡，返回其文本控件（便于「正在解读…」占位就地替换）。</summary>
    private TextBlock AppendBubble(string text, bool isUser, bool isError = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = isError ? ErrorText : PluginUi.TextMain,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
        };
        var bubble = new Border
        {
            Background = isUser ? UserBubbleBg : PluginUi.CardBg,
            BorderBrush = PluginUi.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 3, 0, 3),
            MaxWidth = 330,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = tb,
        };
        _messages.Children.Add(bubble);
        ScrollToEnd();
        return tb;
    }

    private static void SetBubbleText(TextBlock tb, string text, bool isError)
    {
        tb.Text = string.IsNullOrEmpty(text) ? "（空回复）" : text;
        tb.Foreground = isError ? ErrorText : PluginUi.TextMain;
    }

    /// <summary>滚到底部：延后到布局完成后执行，避免新增气泡当帧未测量导致滚动失效。</summary>
    private void ScrollToEnd()
        => Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToEnd()), DispatcherPriority.Background);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
