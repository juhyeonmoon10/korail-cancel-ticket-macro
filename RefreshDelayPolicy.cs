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
        if (intervalSeconds < 1)
            throw new ArgumentOutOfRangeException("intervalSeconds", "조회 간격은 1~2147483647초의 정수로 지정하세요.");
        if (jitterPercent < 0 || jitterPercent > 100)
            throw new ArgumentOutOfRangeException("jitterPercent", "간격 변동 폭은 0~100%로 지정하세요.");
        // Whole seconds and percentages have exact tick endpoints. Even the
        // largest Int32 interval at 100% stays within Int64 arithmetic.
        long baseTicks = (long)intervalSeconds * TimeSpan.TicksPerSecond;
        minimumTicks = Math.Max(TimeSpan.TicksPerSecond, baseTicks * (100 - jitterPercent) / 100);
        maximumTicks = baseTicks * (100 + jitterPercent) / 100;
        minimumSeconds = (double)minimumTicks / TimeSpan.TicksPerSecond;
        maximumSeconds = (double)maximumTicks / TimeSpan.TicksPerSecond;
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
