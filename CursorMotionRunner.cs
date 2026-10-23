using System;
using System.Drawing;
using System.Threading.Tasks;

// Time and transport are supplied so interruption and event order can be tested
// without moving the user's pointer. This runner never clicks or retries.
internal static class CursorMotionRunner
{
    public static async Task RunAsync(CursorMotionPlan plan, Action validate,
        Action<Point> move, Func<int, Task> delay, Func<long> elapsedMilliseconds)
    {
        if (plan == null || validate == null || move == null || delay == null || elapsedMilliseconds == null)
            throw new ArgumentNullException("motion dependency");
        validate();
        if (plan.DurationMilliseconds == 0) return;
        long began = elapsedMilliseconds(), previousTime = began;
        Point previous = plan.PositionAt(0);
        while (true)
        {
            await delay(16);
            validate();
            long now = elapsedMilliseconds();
            // Do not jump to an overdue endpoint after a stalled UI or suspend.
            if (now < previousTime || now - previousTime > 250)
                throw new OperationCanceledException("마우스 이동이 지연되어 입력을 중지했습니다.");
            if (now == previousTime)
                throw new InvalidOperationException("마우스 이동 시간을 확인하지 못했습니다.");
            previousTime = now;
            double progress = Math.Min(1, (now - began) / (double)plan.DurationMilliseconds);
            Point point = plan.PositionAt(progress);
            if (point != previous)
            {
                validate();
                move(point);
                previous = point;
            }
            if (progress >= 1) return;
        }
    }
}
