using System;

// Pure interval policy. The host supplies one random unit per scheduled delay;
// this class neither sends input nor changes any retry or stop condition.
internal sealed class RefreshDelayPolicy
{
    readonly double minimumSeconds, maximumSeconds;
    readonly long minimumTicks, maximumTicks;

    public double MinimumSeconds { get { return minimumSeconds; } }
    public double MaximumSeconds { get { return maximumSeconds; } }

    public RefreshDelayPolicy(int intervalSeconds, int jitterPercent)
    {
        if (intervalSeconds < 30 || intervalSeconds > 300)
            throw new ArgumentOutOfRangeException("intervalSeconds", "조회 간격은 30~300초로 지정하세요.");
        if (jitterPercent < 0 || jitterPercent > 100)
            throw new ArgumentOutOfRangeException("jitterPercent", "간격 변동 폭은 0~100%로 지정하세요.");
        double fraction = jitterPercent / 100.0;
        minimumSeconds = Math.Max(30.0, intervalSeconds * (1.0 - fraction));
        maximumSeconds = Math.Min(300.0, intervalSeconds * (1.0 + fraction));
        // Integer seconds and percent produce hundredth-second endpoints.
        // Round once to represent those endpoints exactly at TimeSpan precision.
        minimumTicks = (long)Math.Round(minimumSeconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
        maximumTicks = (long)Math.Round(maximumSeconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
    }

    public TimeSpan Sample(double unit)
    {
        if (Double.IsNaN(unit) || Double.IsInfinity(unit) || unit < 0 || unit >= 1)
            throw new ArgumentOutOfRangeException("unit", "샘플 값은 0 이상 1 미만의 유한한 수여야 합니다.");
        long range = maximumTicks - minimumTicks;
        if (range == 0) return TimeSpan.FromTicks(minimumTicks);
        // Flooring assigns equal unit intervals to each tick. The clamp only
        // protects the exclusive upper endpoint from floating-point rounding.
        long offset = Math.Min(range - 1, (long)(unit * range));
        return TimeSpan.FromTicks(minimumTicks + offset);
    }
}
