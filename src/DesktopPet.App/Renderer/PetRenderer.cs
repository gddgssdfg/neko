using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Core;

namespace DesktopPet.App.Renderer;

/// <summary>
/// 像素渲染器：用 DrawingVisual + 雪碧图绘制宠物。
/// 眼球独立分层叠加，眼神追踪偏移由渲染器内部维护。
/// </summary>
public sealed class PetRenderer
{
    private SpriteAtlas _atlas;   // v0.1.48：去掉 readonly（支持 SetAtlas 热切换皮肤）
    private readonly FrameAnimator _animator = new();
    private readonly Random _rng = new();

    // 眼球偏移（眼神追踪）
    private Vector _eyeOffset;
    // v0.1.57：眨眼 3 步序列 open → half（半闭）→ closed → open。total ~150ms，半闭软化硬切
    // v0.1.58(任务5)：相位推进改用"绝对截止时间"——Half/Closed 的结束时刻都锚定 _blinkStartMs，
    // 由真实时钟（每 tick 累加宿主传入的真实 dt）判定到点；不再"每 tick 加固定步长"，
    // DispatcherTimer 抖动不会累积成相位时长误差（相位只受 tick 粒度量化，活动期 50ms → 显示 ~100/50ms）。
    private enum BlinkPhase { Open, Half, Closed }
    private BlinkPhase _blinkPhase = BlinkPhase.Open;
    private double _clockMs;       // 渲染器内部单调毫秒钟：每 tick 累加真实 elapsed，非 tick 计数
    private double _blinkStartMs;  // 本次眨眼开始（_clockMs 基准）；Half 结束 = start+80，Open 结束 = start+140
    private double _nextBlinkAtMs; // 下次眨眼开始（_clockMs 基准）；Open 相位下到达即触发
    private double _moodIntensity = 1.0; // v0.1.1 fix：必须显式初始化为 1.0，否则默认 0 → Playback=0 → fps=0 → FrameAnimator.Tick 早返回 → 帧永远停在 0（SetMood 只在插件主动调时才设）
    // v0.1.59(P4)：闪眼/打字活跃的截止时间由 DateTime.Now 改为 _clockMs 基准（与眨眼口径统一）。
    // 语义不变（都是"从现在起持续 ms 毫秒"），但不再受系统时钟跳变影响，且与 Tick 单调时钟一致。
    private double _flashEyesUntilMs;   // 闪眼截止（_clockMs 基准）；默认 0，_clockMs 从 0 起单调增 → 初始即视为已过期
    private string? _speechText;
    private double _speechTimer;

    // v0.1.47：打字互动（猫对键盘活动的反应）
    private double _typingUntilMs;                      // 打字活跃截止（_clockMs 基准）；GlobalInput 每 300ms 节流刷新
    private double _breathOffset;                       // 身体 Y 偏移（呼吸/打字晃动感）
    private double _eyeLookDownOffset = 1.5;            // 打字时眼球下移（"看键盘"感）

    /// <summary>帧变化时触发，宿主据此重绘</summary>
    public event Action? RenderInvalidated;

    /// <summary>v0.1.40：Say 触发时通知宿主显示像素云朵气泡（text, durationMs）。
    /// 插件调 Pet.Say → 本事件 → PetWindow → BubbleWindow。</summary>
    public event Action<string, int>? SpeechRequested;

    /// <summary>v0.1.57：眨眼 3 步序列 open → half(80ms) → closed(60ms) → open；总 ~150ms（与原 100ms 持平/略长，Phase 内平滑）
    /// HasTransientVisual 揭示"瞬时活动"——宿主据此保持主循环高频 tick（位移/拖拽/打字外加 blink/flash/speech）。</summary>
    public bool HasTransientVisual =>
        _blinkPhase != BlinkPhase.Open ||
        _clockMs < _typingUntilMs ||
        _clockMs < _flashEyesUntilMs ||
        _speechTimer > 0;

    // v0.1.58(任务5)：眨眼相位时长（毫秒）。锚定 _blinkStartMs：Half 到 start+HalfMs、Open 到 start+TotalMs。
    // 名义值 80/60ms（总 140ms）；活动期 tick 50ms 量化后实际显示约 Half 100ms + Closed 50ms = 150ms。
    private const double BlinkHalfMs = 80;
    private const double BlinkClosedMs = 60;
    private const double BlinkTotalMs = BlinkHalfMs + BlinkClosedMs;

    public PetRenderer(SpriteAtlas atlas)
    {
        _atlas = atlas;
        // 首次眨眼安排在启动后 2~6s（_clockMs 从 0 起）
        _nextBlinkAtMs = 2000 + _rng.NextDouble() * 4000;
    }

    /// <summary>v0.1.48：热切换皮肤（运行时换 atlas）。清 _animator 缓存强制下帧从新 atlas 拉帧。</summary>
    public void SetAtlas(SpriteAtlas atlas)
    {
        _atlas = atlas;
        _animator.SetState(null);
        RenderInvalidated?.Invoke();
    }

    public SpriteAtlas Atlas => _atlas;
    public double Scale { get; set; } = 3.2; // 与 AppConfig.Scale 默认值保持一致（32px ×3.2 ≈ 102px）

    public void Play(PetState state, bool loop = true)
    {
        var name = state.ToString().ToLowerInvariant();
        _animator.SetState(_atlas.States.GetValueOrDefault(name));
        RenderInvalidated?.Invoke();
    }

    public void SetMood(PetMood mood)
    {
        // v1：情绪影响帧率倍率，后续扩展
        _moodIntensity = mood switch
        {
            PetMood.Happy => 1.3,
            PetMood.Sleepy => 0.6,
            _ => 1.0,
        };
    }

    /// <summary>v0.1.47：标记打字活跃（身体晃动 + 频繁眨眼 + 眼球下移看键盘），持续到 now+duration。
    /// PetWindow.OnInputReaction 每 300ms 节流调用一次，保持打字时猫一直"有反应"。</summary>
    public void SetTypingActive(TimeSpan duration)
    {
        // v0.1.59(P4)：截止时间改用 _clockMs 基准（+ms）；下次 Tick 会把它推进到真实时刻。
        var until = _clockMs + duration.TotalMilliseconds;
        if (until > _typingUntilMs) _typingUntilMs = until;
    }

    public void Say(string text, int durationMs)
    {
        _speechText = text;
        _speechTimer = durationMs / 1000.0;
        // v0.1.40：转发给宿主渲染像素云朵气泡（_speechText 保留为旧兼容，Draw 不再直接画）
        SpeechRequested?.Invoke(text, durationMs);
    }

    public void Tick(TimeSpan delta)
    {
        // v0.1.58(任务5)：单调时钟先行累加真实 elapsed（眨眼/闪眼/打字全部锚定它）
        _clockMs += delta.TotalMilliseconds;

        _animator.Playback = _moodIntensity;
        var changed = _animator.Tick(delta);

        // v0.1.47：打字活跃检测 → 眨眼更频繁 + 身体呼吸晃动（看键盘效果在 Draw 里）
        bool typing = _clockMs < _typingUntilMs;
        double blinkMax = typing ? 1.6 : 6.0;   // 眨眼间隔上限：打字 1.6s / 闲置 6s（间隔分布见 ScheduleNextBlink）
        if (typing)
        {
            // sin 呼吸：幅度 1.5px，周期约 1.4s；身体随打字轻微起伏（用 _clockMs 秒做相位，行为等价）
            _breathOffset = Math.Sin(_clockMs / 1000.0 * 4.5) * 1.5;
        }
        else
        {
            _breathOffset = 0;
        }

        // v0.1.58(任务5)：眨眼 3 步序列，按"绝对截止时间 + 真实时钟"推进。
        // 每 tick 先把真实 elapsed 累进 _clockMs（等价单调毫秒钟），再与锚定 _blinkStartMs 的
        // 截止时间比较：Half→Closed 在 start+80ms、Closed→Open 在 start+140ms。这样 DispatcherTimer
        // 抖动只会让相位切换"迟到到下一个 tick"，而不会逐 tick 累积成误差。
        switch (_blinkPhase)
        {
            case BlinkPhase.Open:
                if (_clockMs >= _nextBlinkAtMs)
                {
                    _blinkPhase = BlinkPhase.Half;
                    _blinkStartMs = _clockMs;
                    changed = true;
                }
                break;
            case BlinkPhase.Half:
                if (_clockMs - _blinkStartMs >= BlinkTotalMs)
                {
                    // UI 卡顿导致单个长 tick 直接越过整个眨眼窗口：不补显示 Closed，直接回 Open
                    ScheduleNextBlink(blinkMax);
                    changed = true;
                }
                else if (_clockMs - _blinkStartMs >= BlinkHalfMs)
                {
                    _blinkPhase = BlinkPhase.Closed;
                    changed = true;
                }
                break;
            case BlinkPhase.Closed:
                if (_clockMs - _blinkStartMs >= BlinkTotalMs)
                {
                    _blinkPhase = BlinkPhase.Open;
                    ScheduleNextBlink(blinkMax);
                    changed = true;
                }
                break;
        }

        // 气泡计时
        if (_speechTimer > 0)
        {
            _speechTimer -= delta.TotalSeconds;
            if (_speechTimer <= 0) _speechText = null;
        }

        if (changed) RenderInvalidated?.Invoke();
    }

    /// <summary>v0.1.58(任务5)：眨眼结束回到 Open 后，安排下一次眨眼绝对时刻（_clockMs 基准）。
    /// 间隔语义与 v0.1.57 一致：闲置 uniform(2,6]s；打字 uniform(1.6,2]s（更频繁）。</summary>
    private void ScheduleNextBlink(double blinkMax)
    {
        _blinkPhase = BlinkPhase.Open;
        var lo = Math.Min(2.0, blinkMax);
        var hi = Math.Max(2.0, blinkMax);
        _nextBlinkAtMs = _clockMs + (lo + _rng.NextDouble() * (hi - lo)) * 1000.0;
    }

    /// <summary>绘制当前帧到 DrawingContext</summary>
    public void Draw(DrawingContext dc)
    {
        var state = _animator.State;
        if (state is null) return;

        var frame = state.Frames[_animator.FrameIndex];
        // v0.1.47：身体 Y 偏移（打字时呼吸/晃动感）
        var target = new Rect(0, _breathOffset, SpriteAtlas.FrameW * Scale, SpriteAtlas.FrameH * Scale);

        // 身体帧
        dc.DrawImage(frame.Bitmap, target);

        // 眼球层（叠加）—— 键盘反应瞪眼优先，其次 3 步眨眼相位（half/closed），最后正常眼球层
        if (state.EyeLayer is not null)
        {
            BitmapSource? eye;
            if (_clockMs < _flashEyesUntilMs)
            {
                eye = _atlas.GetEye("eyes_scared");
            }
            else
            {
                eye = _blinkPhase switch
                {
                    BlinkPhase.Half => _atlas.GetEye("eyes_half") ?? _atlas.GetEye("eyes_blink"),  // 旧素材缺 half → 退化 blink
                    BlinkPhase.Closed => _atlas.GetEye("eyes_blink"),
                    _ => _atlas.GetEye(state.EyeLayer),
                };
            }
            if (eye is not null)
            {
                // 眼神偏移：眼球层整体平移 ±2px（×Scale）；v0.1.47 打字时眼球下移（看键盘）
                double eyeDown = (_clockMs < _typingUntilMs) ? _eyeLookDownOffset : 0;
                var offset = new Vector(_eyeOffset.X * Scale, _eyeOffset.Y * Scale + eyeDown);
                var eyeRect = new Rect(offset.X, offset.Y, SpriteAtlas.FrameW * Scale, SpriteAtlas.FrameH * Scale);
                dc.DrawImage(eye, eyeRect);
            }
        }

        // 阴影（底部）
        if (_atlas.Shadow is not null)
        {
            var sh = new Rect(
                SpriteAtlas.FrameW * Scale * 0.5 - 16 * Scale,
                SpriteAtlas.FrameH * Scale - 6 * Scale,
                32 * Scale, 16 * Scale);
            dc.DrawImage(_atlas.Shadow, sh);
        }
    }

    /// <summary>眼神追踪：设置眼球偏移（由宿主每帧从鼠标位置计算）。
    /// v0.1.59(⑩)：新增 gain 参数——鼠标路过张望时放大偏移（增益 > 1），默认 1.0 行为不变。</summary>
    public void SetEyeOffset(Vector normalizedDirection, double gain = 1.0)
    {
        _eyeOffset = normalizedDirection * 2 * gain; // 基础限幅 ±2px，按 gain 缩放
    }

    /// <summary>键盘反应（BongoCat 借鉴）：短暂"瞪眼"——眼球层切到 scared，到期自动恢复</summary>
    public void FlashEyes(int ms)
    {
        // v0.1.59(P4)：截止时间改用 _clockMs 基准（+ms）
        _flashEyesUntilMs = _clockMs + ms;
        RenderInvalidated?.Invoke();
    }
}
