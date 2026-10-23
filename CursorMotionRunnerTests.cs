using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

// Fake time and transport only: no Windows input, browser, or network calls.
internal static class CursorMotionRunnerTests
{
    static int count;

    sealed class Fixture
    {
        public long Now = 1000;
        public int Step = 16, Validations, Delays, ClockReads;
        public readonly List<Point> Points = new List<Point>();
        public readonly List<long> Times = new List<long>();
        public readonly List<string> Events = new List<string>();
        public Action OnValidate, OnMove, OnClock;

        public void Validate()
        {
            Validations++; Events.Add("validate");
            if (OnValidate != null) OnValidate();
        }
        public void Move(Point point)
        {
            Events.Add("move"); Points.Add(point); Times.Add(Now);
            if (OnMove != null) OnMove();
        }
        public Task Delay(int milliseconds)
        {
            if (milliseconds <= 0) throw new InvalidOperationException("Nonpositive requested delay");
            Delays++; Events.Add("delay"); Now += Step;
            return Task.FromResult(0);
        }
        public long Elapsed()
        {
            ClockReads++;
            if (OnClock != null) OnClock();
            return Now;
        }
        public Task Run(CursorMotionPlan plan)
        { return CursorMotionRunner.RunAsync(plan, Validate, Move, Delay, Elapsed); }
    }

    static CursorMotionPlan Plan()
    {
        return CursorMotionPlan.Create(new Point(100, 200), new Point(1100, 800),
            new Rectangle(0, 0, 2000, 1200), new Random(17));
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        count++; Console.WriteLine("PASS " + name);
    }
    static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
    static async Task ThrowsSame(Func<Task> action, Exception expected, string name)
    {
        try { await action(); }
        catch (Exception ex) { Check(Object.ReferenceEquals(ex, expected), name); return; }
        throw new InvalidOperationException("Expected original error: " + name);
    }
    static int Main()
    {
        try
        {
            Run().GetAwaiter().GetResult();
            Console.WriteLine("Passed " + count + " cursor motion runner checks (fake clock and transport only)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    static async Task Run()
    {
        CursorMotionPlan plan = Plan();
        var normal = new Fixture();
        await normal.Run(plan);
        Check(normal.Points.Count > 2, "nonzero motion delivers intermediate samples");
        Check(normal.Points[0] != plan.PositionAt(0) && normal.Points[0] != plan.PositionAt(1),
            "first delivered sample is between start and destination");
        Check(normal.Points[normal.Points.Count - 1] == plan.PositionAt(1), "last sample reaches exact destination");
        Check(normal.Events[0] == "validate" && normal.Events[1] == "delay", "initial guard runs before the first delay");
        bool allGuarded = true, allOrdered = true, noDuplicates = true, exactSampling = true;
        long earlierTime = 999; double earlierProjection = -1;
        Point previous = plan.PositionAt(0), start = previous, end = plan.PositionAt(1);
        for (int i = 0; i < normal.Events.Count; i++)
            if (normal.Events[i] == "move" && (i == 0 || normal.Events[i - 1] != "validate")) allGuarded = false;
        for (int i = 0; i < normal.Points.Count; i++)
        {
            Point point = normal.Points[i]; long time = normal.Times[i];
            double projection = (point.X - start.X) * (double)(end.X - start.X) +
                (point.Y - start.Y) * (double)(end.Y - start.Y);
            if (time <= earlierTime || projection < earlierProjection) allOrdered = false;
            if (point == previous) noDuplicates = false;
            if (point != plan.PositionAt(Math.Min(1, (time - 1000) / (double)plan.DurationMilliseconds))) exactSampling = false;
            previous = point; earlierTime = time; earlierProjection = projection;
        }
        Check(allGuarded, "every transport call immediately follows validation");
        Check(allOrdered, "samples progress in time and toward destination in order");
        Check(noDuplicates, "unchanged rounded positions are not delivered twice");
        Check(exactSampling, "fake elapsed time selects the planned position");
        Check(normal.Now >= 1000 + plan.DurationMilliseconds && normal.Now < 1000 + plan.DurationMilliseconds + normal.Step,
            "runner ends at the first completed time sample");

        var before = new Fixture(); var beforeError = new OperationCanceledException("before first input");
        before.OnValidate = delegate { throw beforeError; };
        await ThrowsSame(() => before.Run(plan), beforeError, "initial guard error propagates unchanged");
        Check(before.Points.Count == 0 && before.Delays == 0 && before.ClockReads == 0,
            "initial interruption sends no move and starts no timer");

        var afterWait = new Fixture(); var afterWaitError = new OperationCanceledException("after delay");
        afterWait.OnValidate = delegate { if (afterWait.Delays > 0) throw afterWaitError; };
        await ThrowsSame(() => afterWait.Run(plan), afterWaitError, "post-delay interruption propagates unchanged");
        Check(afterWait.Points.Count == 0 && afterWait.Delays == 1, "post-delay guard prevents the first move");

        var beforeMove = new Fixture(); var beforeMoveError = new OperationCanceledException("immediate move guard");
        beforeMove.OnValidate = delegate { if (beforeMove.Validations == 3) throw beforeMoveError; };
        await ThrowsSame(() => beforeMove.Run(plan), beforeMoveError, "immediate pre-move guard error propagates");
        Check(beforeMove.Points.Count == 0 && beforeMove.Delays == 1,
            "pre-move validation remains effective after clock sampling");

        var middle = new Fixture(); var middleError = new OperationCanceledException("mid motion");
        middle.OnValidate = delegate { if (middle.Points.Count == 2) throw middleError; };
        await ThrowsSame(() => middle.Run(plan), middleError, "mid-motion interruption propagates unchanged");
        Check(middle.Points.Count == 2 && middle.Points[1] != plan.PositionAt(1),
            "interrupted movement sends no later sample or destination jump");

        var failedMove = new Fixture(); var moveError = new InvalidOperationException("transport failure");
        failedMove.OnMove = delegate { throw moveError; };
        await ThrowsSame(() => failedMove.Run(plan), moveError, "transport failure propagates unchanged");
        Check(failedMove.Points.Count == 1 && failedMove.Delays == 1,
            "failed transport is never retried or followed by another sample");

        var stalled = new Fixture { Step = 251 };
        await Throws<OperationCanceledException>(() => stalled.Run(plan), "UI gap above 250 ms cancels motion");
        Check(stalled.Points.Count == 0 && stalled.Delays == 1, "initial stale gap sends no overdue sample");
        var stalledMiddle = new Fixture();
        stalledMiddle.OnMove = delegate { stalledMiddle.Step = 251; };
        await Throws<OperationCanceledException>(() => stalledMiddle.Run(plan), "mid-motion stale UI gap cancels");
        Check(stalledMiddle.Points.Count == 1 && stalledMiddle.Points[0] != plan.PositionAt(1),
            "mid-motion stall sends no catch-up destination jump");
        var boundary = new Fixture { Step = 250 };
        await boundary.Run(plan);
        Check(boundary.Points.Count > 1 && boundary.Points[boundary.Points.Count - 1] == plan.PositionAt(1),
            "250 ms clock gap remains within the accepted boundary");

        var stationary = new Fixture();
        CursorMotionPlan still = CursorMotionPlan.Create(new Point(42, 24), new Point(42, 24),
            new Rectangle(0, 0, 100, 100), new Random(17));
        await stationary.Run(still);
        Check(stationary.Validations == 1 && stationary.Points.Count == 0 && stationary.Delays == 0 && stationary.ClockReads == 0,
            "same-point motion validates without waiting, reading time, or moving");
        stationary.OnValidate = delegate { throw beforeError; };
        await ThrowsSame(() => stationary.Run(still), beforeError, "same-point motion cannot bypass the initial guard");

        var frozen = new Fixture { Step = 0 };
        await Throws<InvalidOperationException>(() => frozen.Run(plan), "nonadvancing clock is rejected");
        Check(frozen.Points.Count == 0 && frozen.Delays == 1, "frozen clock cannot spin or move");
        var reversed = new Fixture { Step = -1 };
        await Throws<OperationCanceledException>(() => reversed.Run(plan), "backward clock is rejected");
        Check(reversed.Points.Count == 0 && reversed.Delays == 1, "backward clock sends no move");
        var reverseMiddle = new Fixture();
        reverseMiddle.OnMove = delegate { reverseMiddle.Step = -1; };
        await Throws<OperationCanceledException>(() => reverseMiddle.Run(plan), "clock reversal after a sample is rejected");
        Check(reverseMiddle.Points.Count == 1, "clock reversal cannot send another sample");

        var failedDelay = new Fixture(); var delayError = new InvalidOperationException("delay failure");
        var failedTask = new TaskCompletionSource<int>(); failedTask.SetException(delayError);
        await ThrowsSame(() => CursorMotionRunner.RunAsync(plan, failedDelay.Validate, failedDelay.Move,
            ms => failedTask.Task, failedDelay.Elapsed), delayError, "asynchronous delay failure propagates unchanged");
        Check(failedDelay.Points.Count == 0 && failedDelay.Validations == 1, "failed delay sends no later move");
        var failedClock = new Fixture(); var clockError = new InvalidOperationException("clock failure");
        failedClock.OnClock = delegate { if (failedClock.ClockReads > 1) throw clockError; };
        await ThrowsSame(() => failedClock.Run(plan), clockError, "elapsed clock failure propagates unchanged");
        Check(failedClock.Points.Count == 0 && failedClock.Delays == 1, "failed clock sends no move or retry");

        var dependencies = new Fixture();
        await Throws<ArgumentNullException>(() => dependencies.Run(null), "null plan rejected");
        await Throws<ArgumentNullException>(() => CursorMotionRunner.RunAsync(plan, null, dependencies.Move, dependencies.Delay, dependencies.Elapsed), "null guard rejected");
        await Throws<ArgumentNullException>(() => CursorMotionRunner.RunAsync(plan, dependencies.Validate, null, dependencies.Delay, dependencies.Elapsed), "null move transport rejected");
        await Throws<ArgumentNullException>(() => CursorMotionRunner.RunAsync(plan, dependencies.Validate, dependencies.Move, null, dependencies.Elapsed), "null delay rejected");
        await Throws<ArgumentNullException>(() => CursorMotionRunner.RunAsync(plan, dependencies.Validate, dependencies.Move, dependencies.Delay, null), "null elapsed clock rejected");
        Check(dependencies.Validations == 0 && dependencies.Delays == 0 && dependencies.Points.Count == 0,
            "invalid dependencies have no side effects");
    }
}
