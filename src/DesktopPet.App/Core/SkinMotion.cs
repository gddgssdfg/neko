using System.Text.Json;

namespace DesktopPet.App.Core;

/// <summary>Optional skin-local motion, in 32px logical units and seconds. Missing block preserves legacy behavior.</summary>
public sealed record SkinMotion
{
    public double? WalkStride { get; init; }
    public double GazeMaxX { get; init; } = .65;
    public double GazeMaxY { get; init; } = .35;
    public double BlinkMinSeconds { get; init; } = 2;
    public double BlinkMaxSeconds { get; init; } = 6;
    public double TypingBlinkMinSeconds { get; init; } = 1.6;
    public double TypingBlinkMaxSeconds { get; init; } = 2;
    public double TypingBob { get; init; } = .25;
    public double TypingBobPeriodSeconds { get; init; } = 1.4;

    public static SkinMotion? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("motion", out var data)) return null;
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("motion 必须为对象");
        double Number(string key, double fallback, double min, double max)
        {
            if (!data.TryGetProperty(key, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n) || !double.IsFinite(n) || n < min || n > max)
                throw new FormatException($"motion.{key} 必须是 {min}–{max} 的有限数字");
            return n;
        }
        var result = new SkinMotion
        {
            WalkStride = data.TryGetProperty("walkStride", out _) ? Number("walkStride", 0, 0, 64) : null,
            GazeMaxX = Number("gazeMaxX", .65, 0, .65), GazeMaxY = Number("gazeMaxY", .35, 0, .35),
            BlinkMinSeconds = Number("blinkMinSeconds", 2, .5, 60), BlinkMaxSeconds = Number("blinkMaxSeconds", 6, .5, 60),
            TypingBlinkMinSeconds = Number("typingBlinkMinSeconds", 1.6, .5, 60), TypingBlinkMaxSeconds = Number("typingBlinkMaxSeconds", 2, .5, 60),
            TypingBob = Number("typingBob", .25, 0, .5), TypingBobPeriodSeconds = Number("typingBobPeriodSeconds", 1.4, .5, 10),
        };
        if (result.BlinkMinSeconds > result.BlinkMaxSeconds || result.TypingBlinkMinSeconds > result.TypingBlinkMaxSeconds)
            throw new FormatException("眨眼间隔最小值不能大于最大值");
        return result;
    }
}

public sealed record SkinTransition(string From, string To, string Via);
