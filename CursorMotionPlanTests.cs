using System;
using System.Collections.Generic;
using System.Drawing;

internal static class CursorMotionPlanTests
{
    static int checks;

    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    static void Rejects(Action action, string parameter, string name)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException error) { rejected = error.ParamName == parameter; }
        Check(rejected, name);
    }

    static double Distance(Point a, Point b)
    {
        double x = (double)a.X - b.X;
        double y = (double)a.Y - b.Y;
        return Math.Sqrt(x * x + y * y);
    }

    static void CheckPath(Point start, Point end, Rectangle bounds, string name)
    {
        for (int seed = 0; seed < 8; seed++)
        {
            var plan = CursorMotionPlan.Create(start, end, bounds, new Random(seed));
            Check(plan.PositionAt(0) == start && plan.PositionAt(1) == end, name + " exact endpoints seed=" + seed);
            Check(plan.DurationMilliseconds >= 180 && plan.DurationMilliseconds <= 750,
                name + " bounded duration seed=" + seed);
            bool inside = true;
            bool forward = true;
            double previousProjection = 0;
            double dx = (double)end.X - start.X;
            double dy = (double)end.Y - start.Y;
            double distance = Distance(start, end);
            for (int sample = 0; sample <= 1000; sample++)
            {
                Point point = plan.PositionAt(sample / 1000.0);
                inside &= point.X >= bounds.X && point.Y >= bounds.Y &&
                    point.X <= (long)bounds.X + bounds.Width - 1 && point.Y <= (long)bounds.Y + bounds.Height - 1;
                double projection = (((double)point.X - start.X) * dx + ((double)point.Y - start.Y) * dy) / distance;
                // Integer pixel rounding may move a projection by at most
                // sqrt(2) pixels even though the underlying curve is monotone.
                forward &= projection >= previousProjection - 1.5 && projection >= -0.75 && projection <= distance + 0.75;
                previousProjection = projection;
            }
            Check(inside, name + " all sampled points inside seed=" + seed);
            Check(forward, name + " no longitudinal overshoot or reversal seed=" + seed);
        }
    }

    static int Main()
    {
        try
        {
            var bounds = new Rectangle(-1920, -1080, 5760, 3240);
            CheckPath(new Point(-1800, -900), new Point(3000, 1800), bounds, "multiple screens with negative origin");
            CheckPath(new Point(-1920, -1080), new Point(3839, 2159), bounds, "opposite inclusive corners");
            CheckPath(new Point(-1600, -1080), new Point(3500, -1080), bounds, "top edge");
            CheckPath(new Point(3839, -900), new Point(3839, 2100), bounds, "right edge");
            CheckPath(new Point(99, 500), new Point(99, -100), new Rectangle(99, -100, 1, 1000), "single column");
            CheckPath(new Point(-500, 32), new Point(100, 32), new Rectangle(-500, 32, 1000, 1), "single row");
            CheckPath(new Point(0, 0), new Point(1, 1), new Rectangle(0, 0, 2, 2), "one pixel diagonal");
            CheckPath(new Point(Int32.MaxValue - 999, Int32.MaxValue - 999), new Point(Int32.MaxValue, Int32.MaxValue),
                new Rectangle(Int32.MaxValue - 999, Int32.MaxValue - 999, 1000, 1000), "highest integer coordinates");
            CheckPath(new Point(Int32.MinValue, Int32.MinValue), new Point(-2, -2),
                new Rectangle(Int32.MinValue, Int32.MinValue, Int32.MaxValue, Int32.MaxValue), "largest negative span");

            var stationaryRandom = new Random(314);
            var stationary = CursorMotionPlan.Create(new Point(5, 8), new Point(5, 8), new Rectangle(5, 8, 1, 1), stationaryRandom);
            Check(stationary.DurationMilliseconds == 0, "same point needs no movement time");
            Check(stationary.PositionAt(0.2) == new Point(5, 8) && stationary.PositionAt(0.8) == new Point(5, 8),
                "same point remains stationary");
            Check(stationaryRandom.Next() == new Random(314).Next(), "same point does not consume randomness");

            var curve = CursorMotionPlan.Create(new Point(100, 400), new Point(1100, 400), new Rectangle(0, 0, 1200, 800), new Random(19));
            Point previous = curve.PositionAt(0);
            bool continuous = true;
            bool singleBow = true;
            int midY = curve.PositionAt(0.5).Y;
            int side = Math.Sign(midY - 400);
            for (int i = 1; i <= 1000; i++)
            {
                Point position = curve.PositionAt(i / 1000.0);
                continuous &= Distance(previous, position) <= 3;
                singleBow &= Math.Sign(position.Y - 400) == 0 || Math.Sign(position.Y - 400) == side;
                previous = position;
            }
            Check(continuous, "dense samples have no jumps beyond pixel quantization");
            Check(singleBow && side != 0, "path bows to one side without zigzags");
            Check(Distance(curve.PositionAt(0), curve.PositionAt(0.05)) < Distance(curve.PositionAt(0.45), curve.PositionAt(0.5)),
                "path accelerates from its start");
            Check(Distance(curve.PositionAt(0.95), curve.PositionAt(1)) < Distance(curve.PositionAt(0.5), curve.PositionAt(0.55)),
                "path decelerates into its endpoint");
            Check(curve.PositionAt(-1) == curve.PositionAt(0) && curve.PositionAt(2) == curve.PositionAt(1),
                "finite progress outside the timeline clamps to endpoints");
            Check(curve.PositionAt(0.3125) == curve.PositionAt(0.3125), "repeated samples do not change the planned curve");

            var random = new Random(12345);
            var durations = new HashSet<int>();
            var midpoints = new HashSet<Point>();
            bool identicalSeeds = true;
            for (int i = 0; i < 40; i++)
            {
                var plan = CursorMotionPlan.Create(new Point(100, 400), new Point(1100, 400), new Rectangle(0, 0, 1200, 800), random);
                durations.Add(plan.DurationMilliseconds);
                midpoints.Add(plan.PositionAt(0.5));
                var first = CursorMotionPlan.Create(new Point(-500, -200), new Point(500, 200), bounds, new Random(i));
                var second = CursorMotionPlan.Create(new Point(-500, -200), new Point(500, 200), bounds, new Random(i));
                identicalSeeds &= first.DurationMilliseconds == second.DurationMilliseconds;
                for (int sample = 0; sample <= 10; sample++)
                    identicalSeeds &= first.PositionAt(sample / 10.0) == second.PositionAt(sample / 10.0);
            }
            Check(durations.Count > 10, "successive plans vary their bounded duration");
            Check(midpoints.Count > 10, "successive plans vary their single curvature");
            Check(identicalSeeds, "seeded plans are deterministic across duration and path");
            var shortPlan = CursorMotionPlan.Create(new Point(0, 0), new Point(1, 0), bounds, new Random(7));
            var longPlan = CursorMotionPlan.Create(new Point(0, 0), new Point(3000, 2000), bounds, new Random(7));
            Check(shortPlan.DurationMilliseconds < longPlan.DurationMilliseconds, "greater distance takes more time with the same random sample");

            Rejects(() => CursorMotionPlan.Create(new Point(), new Point(), new Rectangle(0, 0, 1, 1), null), "random", "null random rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(), new Point(), Rectangle.Empty, new Random(1)), "bounds", "empty bounds rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(), new Point(), new Rectangle(0, 0, -1, 2), new Random(1)), "bounds", "negative width rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(), new Point(), new Rectangle(0, 0, 2, -1), new Random(1)), "bounds", "negative height rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(Int32.MaxValue, 0), new Point(Int32.MaxValue, 0), new Rectangle(Int32.MaxValue, 0, 2, 1), new Random(1)),
                "bounds", "bounds beyond highest x coordinate rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(0, Int32.MaxValue), new Point(0, Int32.MaxValue), new Rectangle(0, Int32.MaxValue, 1, 2), new Random(1)),
                "bounds", "bounds beyond highest y coordinate rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(-1, 0), new Point(), new Rectangle(0, 0, 2, 2), new Random(1)), "start", "start before bounds rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(), new Point(2, 0), new Rectangle(0, 0, 2, 2), new Random(1)), "end", "exclusive right endpoint rejected");
            Rejects(() => CursorMotionPlan.Create(new Point(), new Point(0, 2), new Rectangle(0, 0, 2, 2), new Random(1)), "end", "exclusive bottom endpoint rejected");
            Rejects(() => curve.PositionAt(Double.NaN), "progress", "NaN progress rejected");
            Rejects(() => curve.PositionAt(Double.PositiveInfinity), "progress", "infinite progress rejected");
            Rejects(() => curve.PositionAt(Double.NegativeInfinity), "progress", "negative infinite progress rejected");

            Console.WriteLine("PASS " + checks + " cursor motion checks (pure paths; no native input or network).");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
