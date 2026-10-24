using System.Windows;

namespace DesktopPet.App.Windows;

/// <summary>Place an overlay near its anchor, using available space between companion windows.</summary>
public static class CompanionLayout
{
    public static Point Place(Size size, Rect work, IReadOnlyList<Rect> obstacles)
    {
        const double gap = 12;
        var anchor = obstacles.Count > 0 ? obstacles[0] : work;
        var xs = new List<double> { anchor.Right + gap, anchor.Left - size.Width - gap, work.Left, work.Right - size.Width };
        var ys = new List<double> { anchor.Top + (anchor.Height - size.Height) / 2, work.Top, work.Bottom - size.Height };
        foreach (var rect in obstacles)
        {
            xs.Add(rect.Left - size.Width - gap); xs.Add(rect.Right + gap);
            ys.Add(rect.Top - size.Height - gap); ys.Add(rect.Bottom + gap);
        }
        Point best = new(work.Left, work.Top);
        double bestScore = double.PositiveInfinity;
        foreach (var x in xs)
        foreach (var y in ys)
        {
            var point = new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width)),
                Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
            var candidate = new Rect(point, size);
            double overlap = 0;
            foreach (var rect in obstacles)
            {
                var padded = rect; padded.Inflate(gap, gap);
                var intersection = Rect.Intersect(candidate, padded);
                if (!intersection.IsEmpty) overlap += intersection.Width * intersection.Height;
            }
            var dx = candidate.Left + size.Width / 2 - (anchor.Left + anchor.Width / 2);
            var dy = candidate.Top + size.Height / 2 - (anchor.Top + anchor.Height / 2);
            double score = overlap * 1e8 + dx * dx + dy * dy;
            if (score < bestScore) { bestScore = score; best = point; }
        }
        return best;
    }
}
