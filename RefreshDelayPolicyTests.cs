using System;

internal static class RefreshDelayPolicyTests
{
    static int checks;
    static readonly double BelowOne = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0) - 1);

    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static bool Near(double actual, double expected)
    { return Math.Abs(actual - expected) <= 0.0000001; }
    static void Rejects(Action action, string parameter, string name)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentOutOfRangeException error) { rejected = error.ParamName == parameter; }
        Check(rejected, name);
    }

    static int Main()
    {
        try
        {
            var normal = new RefreshDelayPolicy(60, 20);
            Check(Near(normal.MinimumSeconds, 48) && Near(normal.MaximumSeconds, 72), "60 seconds plus or minus twenty percent gives 48 to 72 seconds");
            Check(normal.Sample(0).Ticks == 48 * TimeSpan.TicksPerSecond, "zero unit includes the minimum endpoint");
            Check(Near(normal.Sample(0.25).TotalSeconds, 54) && Near(normal.Sample(0.5).TotalSeconds, 60) && Near(normal.Sample(0.75).TotalSeconds, 66),
                "quartiles follow a uniform linear interval");
            Check(normal.Sample(BelowOne).Ticks == 72 * TimeSpan.TicksPerSecond - 1, "largest valid unit remains one tick below the upper endpoint");
            Check(normal.Sample(0.12345) == normal.Sample(0.12345), "sampling depends only on the supplied unit without internal random state");

            var lower = new RefreshDelayPolicy(30, 20);
            Check(Near(lower.MinimumSeconds, 30) && Near(lower.MaximumSeconds, 36), "minimum base clips its lower bound to thirty seconds");
            Check(Near(lower.Sample(0.5).TotalSeconds, 33), "clipped lower interval samples its own midpoint without accumulating at the cap");
            var upper = new RefreshDelayPolicy(300, 20);
            Check(Near(upper.MinimumSeconds, 240) && Near(upper.MaximumSeconds, 300), "maximum base clips its upper bound to three hundred seconds");
            Check(Near(upper.Sample(0.5).TotalSeconds, 270), "clipped upper interval samples its own midpoint");
            var both = new RefreshDelayPolicy(200, 100);
            Check(Near(both.MinimumSeconds, 30) && Near(both.MaximumSeconds, 300), "one hundred percent can clip both bounds");
            Check(Near(both.Sample(0.25).TotalSeconds, 97.5) && Near(both.Sample(0.5).TotalSeconds, 165) && Near(both.Sample(0.75).TotalSeconds, 232.5),
                "both clipped bounds still sample the effective interval uniformly");
            var smallest = new RefreshDelayPolicy(30, 100);
            Check(Near(smallest.MinimumSeconds, 30) && Near(smallest.MaximumSeconds, 60), "thirty seconds with one hundred percent never samples below thirty");
            var fractional = new RefreshDelayPolicy(31, 1);
            Check(Near(fractional.MinimumSeconds, 30.69) && Near(fractional.MaximumSeconds, 31.31), "one percent preserves fractional-second bounds");
            Check(fractional.Sample(0).Ticks == 306900000L && fractional.Sample(BelowOne).Ticks == 313099999L,
                "fractional bounds and exclusive maximum retain tick precision");

            foreach (int interval in new [] { 30, 31, 60, 299, 300 })
            {
                var fixedDelay = new RefreshDelayPolicy(interval, 0);
                Check(fixedDelay.MinimumSeconds == interval && fixedDelay.MaximumSeconds == interval &&
                    fixedDelay.Sample(0).TotalSeconds == interval && fixedDelay.Sample(0.5).TotalSeconds == interval && fixedDelay.Sample(BelowOne).TotalSeconds == interval,
                    "zero percent is a fixed delay at base " + interval);
            }
            foreach (int interval in new [] { Int32.MinValue, -1, 0, 29, 301, Int32.MaxValue })
            {
                int captured = interval;
                Rejects(() => new RefreshDelayPolicy(captured, 20), "intervalSeconds", "invalid interval rejected: " + interval);
            }
            foreach (int percent in new [] { Int32.MinValue, -1, 101, Int32.MaxValue })
            {
                int captured = percent;
                Rejects(() => new RefreshDelayPolicy(60, captured), "jitterPercent", "invalid percentage rejected: " + percent);
            }
            foreach (double unit in new [] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, -Double.Epsilon, -0.5, 1.0, 1.5, Double.MaxValue })
            {
                double captured = unit;
                Rejects(() => normal.Sample(captured), "unit", "invalid sample unit rejected: " + unit);
                Rejects(() => new RefreshDelayPolicy(60, 0).Sample(captured), "unit", "fixed mode still rejects invalid sample unit: " + unit);
            }

            bool allBounds = true, allMonotone = true, allMidpoints = true;
            double[] units = { 0, Double.Epsilon, 0.125, 0.25, 0.5, 0.75, 0.875, BelowOne };
            // Cover every supported setting pair, including caps and tiny ranges.
            for (int interval = 30; interval <= 300; interval++)
            {
                for (int percent = 0; percent <= 100; percent++)
                {
                    var policy = new RefreshDelayPolicy(interval, percent);
                    long minimum = (long)Math.Round(policy.MinimumSeconds * TimeSpan.TicksPerSecond);
                    long maximum = (long)Math.Round(policy.MaximumSeconds * TimeSpan.TicksPerSecond);
                    long previous = minimum;
                    foreach (double unit in units)
                    {
                        long ticks = policy.Sample(unit).Ticks;
                        allBounds &= ticks >= 30 * TimeSpan.TicksPerSecond && ticks <= 300 * TimeSpan.TicksPerSecond &&
                            ticks >= minimum && (minimum == maximum ? ticks == maximum : ticks < maximum);
                        allMonotone &= ticks >= previous;
                        previous = ticks;
                    }
                    allMidpoints &= Math.Abs(policy.Sample(0.5).Ticks - (minimum + (maximum - minimum) / 2)) <= 1;
                }
            }
            Check(allBounds, "every valid base and percentage stays in its effective range and global caps");
            Check(allMonotone, "every valid policy is monotone across ordered sample units");
            Check(allMidpoints, "every valid policy samples the effective midpoint within one tick");
            Console.WriteLine("PASS " + checks + " refresh delay policy checks (pure math; no random generator, scheduling or input).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
