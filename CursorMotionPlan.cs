using System;
using System.Drawing;

// A pure, bounded path for visible cursor animation. Sampling does not consume
// randomness or send input; the caller owns timing and all input safety checks.
internal sealed class CursorMotionPlan
{
    readonly Point start;
    readonly Point end;
    readonly double controlX;
    readonly double controlY;
    readonly int minimumX;
    readonly int minimumY;
    readonly int maximumX;
    readonly int maximumY;

    public int DurationMilliseconds { get; private set; }

    CursorMotionPlan(Point start, Point end, Rectangle bounds, int maximumX, int maximumY,
        double controlX, double controlY, int durationMilliseconds)
    {
        this.start = start;
        this.end = end;
        minimumX = bounds.X;
        minimumY = bounds.Y;
        this.maximumX = maximumX;
        this.maximumY = maximumY;
        this.controlX = controlX;
        this.controlY = controlY;
        DurationMilliseconds = durationMilliseconds;
    }

    public static CursorMotionPlan Create(Point start, Point end, Rectangle bounds, Random random)
    {
        if (random == null) throw new ArgumentNullException("random");
        // Rectangle.Right/Bottom can overflow even when the last included pixel
        // is valid. Use long arithmetic and the inclusive final pixel instead.
        long lastX = (long)bounds.X + bounds.Width - 1L;
        long lastY = (long)bounds.Y + bounds.Height - 1L;
        if (bounds.Width <= 0 || bounds.Height <= 0 || lastX > Int32.MaxValue || lastY > Int32.MaxValue)
            throw new ArgumentOutOfRangeException("bounds", "Cursor bounds must contain representable pixels.");
        if (!Contains(start, bounds, lastX, lastY)) throw new ArgumentOutOfRangeException("start");
        if (!Contains(end, bounds, lastX, lastY)) throw new ArgumentOutOfRangeException("end");
        if (start == end)
            return new CursorMotionPlan(start, end, bounds, (int)lastX, (int)lastY, start.X, start.Y, 0);

        double dx = (double)end.X - start.X;
        double dy = (double)end.Y - start.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        // Distance affects the overall pace; a bounded per-plan factor prevents
        // either a very long animation or a jump for a short but nonzero move.
        double baseDuration = 210.0 + 480.0 * (distance / (distance + 800.0));
        int duration = (int)Math.Round(baseDuration * (0.90 + 0.18 * random.NextDouble()));
        duration = Math.Max(180, Math.Min(750, duration));

        double middleX = start.X + dx / 2.0;
        double middleY = start.Y + dy / 2.0;
        double normalX = -dy / distance;
        double normalY = dx / distance;
        double positiveRoom = OffsetRoom(middleX, middleY, normalX, normalY, bounds, lastX, lastY);
        double negativeRoom = OffsetRoom(middleX, middleY, -normalX, -normalY, bounds, lastX, lastY);
        double sign = random.NextDouble() < 0.5 ? -1.0 : 1.0;
        if (sign > 0 && positiveRoom == 0 && negativeRoom > 0) sign = -1;
        else if (sign < 0 && negativeRoom == 0 && positiveRoom > 0) sign = 1;
        double desiredOffset = Math.Min(96.0, distance * 0.12) * (0.4 + 0.6 * random.NextDouble());
        double offset = Math.Min(desiredOffset, sign > 0 ? positiveRoom : negativeRoom) * sign;
        double controlX = Clamp(middleX + normalX * offset, bounds.X, lastX);
        double controlY = Clamp(middleY + normalY * offset, bounds.Y, lastY);

        // A quadratic Bezier stays in the convex hull of its three points.
        // Constraining the one control point gives one bow, no overshoot, and
        // keeps the whole path inside the rectangle, including screen edges.
        return new CursorMotionPlan(start, end, bounds, (int)lastX, (int)lastY,
            controlX, controlY, duration);
    }

    public Point PositionAt(double progress)
    {
        if (Double.IsNaN(progress) || Double.IsInfinity(progress))
            throw new ArgumentOutOfRangeException("progress");
        if (progress <= 0) return start;
        if (progress >= 1) return end;
        // Smoothstep has zero velocity at both endpoints. Integer rounding is
        // the only quantization; the curve itself is continuous and monotone
        // along the start-to-end axis.
        double t = progress * progress * (3.0 - 2.0 * progress);
        double remaining = 1.0 - t;
        double x = remaining * remaining * start.X + 2.0 * remaining * t * controlX + t * t * end.X;
        double y = remaining * remaining * start.Y + 2.0 * remaining * t * controlY + t * t * end.Y;
        return new Point((int)Clamp(Math.Round(x, MidpointRounding.AwayFromZero), minimumX, maximumX),
            (int)Clamp(Math.Round(y, MidpointRounding.AwayFromZero), minimumY, maximumY));
    }

    static bool Contains(Point point, Rectangle bounds, long lastX, long lastY)
    {
        return point.X >= bounds.X && point.Y >= bounds.Y && point.X <= lastX && point.Y <= lastY;
    }

    static double OffsetRoom(double x, double y, double directionX, double directionY,
        Rectangle bounds, long lastX, long lastY)
    {
        double room = Double.PositiveInfinity;
        if (directionX > 0) room = Math.Min(room, (lastX - x) / directionX);
        else if (directionX < 0) room = Math.Min(room, (bounds.X - x) / directionX);
        if (directionY > 0) room = Math.Min(room, (lastY - y) / directionY);
        else if (directionY < 0) room = Math.Min(room, (bounds.Y - y) / directionY);
        return Math.Max(0, room);
    }

    static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}
