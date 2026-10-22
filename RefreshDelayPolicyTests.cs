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

            var lower = new RefreshDelayPolicy(5, 20);
            Check(Near(lower.MinimumSeconds, 4) && Near(lower.MaximumSeconds, 6), "five seconds plus or minus twenty percent gives four to six seconds");
            Check(Near(lower.Sample(0.5).TotalSeconds, 5), "five-second interval is not silently raised to thirty seconds");
            var upper = new RefreshDelayPolicy(300, 20);
            Check(Near(upper.MinimumSeconds, 240) && Near(upper.MaximumSeconds, 360), "three-hundred-second interval has no artificial upper cap");
            Check(Near(upper.Sample(0.5).TotalSeconds, 300), "unclipped upper interval samples its own midpoint");
            var both = new RefreshDelayPolicy(200, 100);
            Check(Near(both.MinimumSeconds, 1) && Near(both.MaximumSeconds, 400), "one hundred percent floors only the minimum at one second");
            Check(Near(both.Sample(0.25).TotalSeconds, 100.75) && Near(both.Sample(0.5).TotalSeconds, 200.5) && Near(both.Sample(0.75).TotalSeconds, 300.25),
                "one-second floor still samples the effective interval uniformly");
            var smallest = new RefreshDelayPolicy(1, 100);
            Check(Near(smallest.MinimumSeconds, 1) && Near(smallest.MaximumSeconds, 2), "one second with one hundred percent cannot produce a zero delay");
            var fractional = new RefreshDelayPolicy(31, 1);
            Check(Near(fractional.MinimumSeconds, 30.69) && Near(fractional.MaximumSeconds, 31.31), "one percent preserves fractional-second bounds");
            Check(fractional.Sample(0).Ticks == 306900000L && fractional.Sample(BelowOne).Ticks == 313099999L,
                "fractional bounds and exclusive maximum retain tick precision");

            foreach (int interval in new [] { 1, 5, 29, 30, 31, 60, 299, 300, 301, 999999, 9999999, Int32.MaxValue })
            {
                var fixedDelay = new RefreshDelayPolicy(interval, 0);
                Check(fixedDelay.MinimumSeconds == interval && fixedDelay.MaximumSeconds == interval &&
                    fixedDelay.Sample(0).TotalSeconds == interval && fixedDelay.Sample(0.5).TotalSeconds == interval && fixedDelay.Sample(BelowOne).TotalSeconds == interval,
                    "zero percent is a fixed delay at base " + interval);
            }
            foreach (int interval in new [] { Int32.MinValue, -1, 0 })
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

            var huge = new RefreshDelayPolicy(Int32.MaxValue, 100);
            long twiceMaxTicks = 2L * Int32.MaxValue * TimeSpan.TicksPerSecond;
            Check(huge.MinimumSeconds == 1 && huge.MaximumSeconds == 2.0 * Int32.MaxValue,
                "largest interval retains its full one-hundred-percent range");
            Check(huge.Sample(BelowOne).Ticks < twiceMaxTicks && huge.Sample(BelowOne).Ticks > twiceMaxTicks - 100,
                "largest range keeps its exclusive endpoint without overflow");
            DateTime future = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc) + huge.Sample(BelowOne);
            Check(future.Year > 2100 && future.Year < 2200, "largest configured delay can be scheduled from the current era");
            var half = new RefreshDelayPolicy(Int32.MaxValue, 50);
            Check(half.Sample(0).Ticks == (long)Int32.MaxValue * TimeSpan.TicksPerSecond / 2 &&
                half.Sample(BelowOne).Ticks < (long)Int32.MaxValue * TimeSpan.TicksPerSecond * 3 / 2,
                "largest interval preserves fifty-percent endpoints exactly");

            bool allBounds = true, allMonotone = true, allMidpoints = true;
            double[] units = { 0, Double.Epsilon, 0.125, 0.25, 0.5, 0.75, 0.875, BelowOne };
            // Cover all percentages across short intervals and large boundaries.
            int[] largeIntervals = { 301, 999999, 9999999, Int32.MaxValue - 1, Int32.MaxValue };
            for (int sample = 1; sample <= 300 + largeIntervals.Length; sample++)
            {
                int interval = sample <= 300 ? sample : largeIntervals[sample - 301];
                for (int percent = 0; percent <= 100; percent++)
                {
                    var policy = new RefreshDelayPolicy(interval, percent);
                    long baseTicks = (long)interval * TimeSpan.TicksPerSecond;
                    long minimum = Math.Max(TimeSpan.TicksPerSecond, baseTicks * (100 - percent) / 100);
                    long maximum = baseTicks * (100 + percent) / 100;
                    long previous = minimum;
                    foreach (double unit in units)
                    {
                        long ticks = policy.Sample(unit).Ticks;
                        allBounds &= ticks >= TimeSpan.TicksPerSecond &&
                            ticks >= minimum && (minimum == maximum ? ticks == maximum : ticks < maximum);
                        allMonotone &= ticks >= previous;
                        previous = ticks;
                    }
                    allMidpoints &= Math.Abs(policy.Sample(0.5).Ticks - (minimum + (maximum - minimum) / 2)) <= 1;
                }
            }
            Check(allBounds, "short and boundary intervals stay in their effective range above zero");
            Check(allMonotone, "every valid policy is monotone across ordered sample units");
            Check(allMidpoints, "every valid policy samples the effective midpoint within one tick");
            Console.WriteLine("PASS " + checks + " refresh delay policy checks (pure math; no random generator, scheduling or input).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
