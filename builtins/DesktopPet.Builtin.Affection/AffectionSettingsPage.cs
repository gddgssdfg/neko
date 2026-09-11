using System.Windows;
using System.Windows.Controls;
using DesktopPet.Core;
using DesktopPet.Sdk;

namespace DesktopPet.Builtin.Affection;

/// <summary>
/// 好感度插件的自定义设置页（v0.1.5 ISettingsPage 接入示范；v0.1.63 改用 PluginUi 统一深色风格）。
/// 显示当前好感度分数 + 重置按钮。
/// Title 必须与 AffectionPlugin.Id 匹配，设置窗口按此嵌入右侧详情区。
/// </summary>
public sealed class AffectionSettingsPage : ISettingsPage
{
    private readonly IPluginStorage _storage;
    private readonly Func<int> _getScore;
    private const string ScoreKey = "score";

    public AffectionSettingsPage(IPluginStorage storage, Func<int> getScore)
    {
        _storage = storage;
        _getScore = getScore;
    }

    public string Id => "com.lantern.affection";
    public string Title => "com.lantern.affection";

    public object BuildContent()
    {
        var stack = new StackPanel { Margin = new Thickness(8) };

        var scoreText = PluginUi.Text("");
        scoreText.FontSize = 14;
        scoreText.Margin = new Thickness(0, 0, 0, 4);

        var hint = PluginUi.Text("重置后下次抚摸起从 0 重新累计", sub: true);
        hint.FontSize = 11;
        hint.Margin = new Thickness(0, 0, 0, 8);

        var resetBtn = PluginUi.Button("重置好感度");
        resetBtn.Padding = new Thickness(8, 4, 8, 4);

        void Refresh()
        {
            var s = _getScore();
            scoreText.Text = $"当前好感度分数：{s}";
        }
        Refresh();

        resetBtn.Click += (_, _) =>
        {
            _storage.Set(ScoreKey, 0);
            Refresh();
            MessageBox.Show("好感度已重置为 0", "好感度设置", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        stack.Children.Add(scoreText);
        stack.Children.Add(hint);
        stack.Children.Add(resetBtn);
        return stack;
    }
}
