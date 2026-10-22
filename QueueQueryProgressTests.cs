using System;

// Deterministic observations only. No timers, browser, network, or mouse input.
internal static class QueueQueryProgressTests
{
    static int count;
    static readonly DateTime Start = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
    const string Source = "https://www.korail.com/ticket/search/list";
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static int Main()
    {
        try { Run(); Console.WriteLine("Passed " + count + " queue query progress checks (observations only)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run()
    {
        var progress = new QueueQueryProgress();
        Check(!progress.Ready(2, Source, 5, "matching rows", Start) && !progress.Ready(2, Source, 5, "matching rows", Start.AddSeconds(5)), "matching rows never authorize an unsubmitted query");
        progress.Submit(2, Source, 5);
        Check(!progress.Ready(2, Source, 5, "matching rows", Start) && !progress.Ready(2, Source, 5, "matching rows", Start.AddMinutes(10)), "unchanged old results never become fresh merely by waiting");
        Check(!progress.Ready(2, Source, 5, "different visible text", Start.AddMinutes(10)), "a fingerprint alone cannot replace navigation or observed result mutation proof");

        progress.Submit(2, Source, 5);
        Check(!progress.Ready(3, Source, 0, "fresh rows", Start), "new document at the same URL begins stabilization rather than approving immediately");
        Check(!progress.Ready(3, Source, 0, "fresh rows", Start.AddMilliseconds(749)), "navigation proof still requires the complete 750 millisecond stable period");
        Check(progress.Ready(3, Source, 0, "fresh rows", Start.AddMilliseconds(750)), "fresh navigation and a stable result approve at the 750 millisecond boundary");

        progress.Submit(4, "https://www.korail.com/ticket/search/general", 0);
        Check(!progress.Ready(4, Source, 0, "queried rows", Start) && progress.Ready(4, Source, 0, "queried rows", Start.AddMilliseconds(750)), "SPA URL transition supplies query progress but still waits for stability");
        progress.Submit(4, Source, 8);
        Check(!progress.Ready(4, Source, 9, "same rendered train text", Start), "same URL result mutation starts a new stable period");
        Check(progress.Ready(4, Source, 9, "same rendered train text", Start.AddMilliseconds(750)), "a proved same URL refresh can legitimately return identical train text");

        progress.Submit(4, Source, 8);
        Check(!progress.Ready(4, Source, 9, "partial rows", Start) && !progress.Ready(4, Source, 10, "complete rows", Start.AddMilliseconds(700)), "changing the row fingerprint restarts stabilization");
        Check(!progress.Ready(4, Source, 10, "complete rows", Start.AddMilliseconds(1449)), "time spent on partial rows does not count toward a completed list");
        Check(progress.Ready(4, Source, 10, "complete rows", Start.AddMilliseconds(1450)), "only the complete unchanged list earns approval");

        progress.Unstable();
        Check(!progress.Ready(4, Source, 10, "complete rows", Start.AddMilliseconds(2000)), "explicit loading or unknown state invalidates an earlier stable approval");
        Check(!progress.Ready(4, Source, 10, "complete rows", Start.AddMilliseconds(2749)) && progress.Ready(4, Source, 10, "complete rows", Start.AddMilliseconds(2750)), "resuming after an unstable observation requires a fresh 750 milliseconds");

        progress.Reset();
        Check(!progress.Ready(4, Source, 10, "complete rows", Start.AddSeconds(4)) && !progress.Ready(5, Source, 0, "complete rows", Start.AddSeconds(10)), "reset removes submission authority as well as the old stable result");
        progress.Submit(5, Source, 0);
        Check(!progress.Ready(5, Source, 1, "complete rows", Start.AddSeconds(11)), "a later submission cannot inherit an earlier job's stable approval");
        Check(progress.Ready(5, Source, 1, "complete rows", Start.AddMilliseconds(11750)), "the later submitted job can independently establish fresh stability");

        progress.Submit(6, Source, 3);
        Check(!progress.Ready(7, Source, 0, null, Start) && !progress.Ready(7, Source, 0, "", Start.AddSeconds(2)), "fresh navigation never authorizes a null or empty fingerprint");
        Check(!progress.Ready(7, Source, 0, "returned rows", Start.AddSeconds(3)), "the first real result after empty evidence begins its own stable period");
        Check(!progress.Ready(7, Source, 0, "", Start.AddMilliseconds(3500)), "an intervening missing fingerprint interrupts result confirmation");
        Check(!progress.Ready(7, Source, 0, "returned rows", Start.AddMilliseconds(3800)), "missing evidence cannot be bridged to reuse an earlier stability timestamp");
        Check(progress.Ready(7, Source, 0, "returned rows", Start.AddMilliseconds(4550)), "reappeared results approve only after another uninterrupted stable period");

        progress.Submit(8, Source, 3);
        Check(!progress.Ready(8, Source, 4, "rows", Start), "new revision proof starts another independent query");
        Check(!progress.Ready(8, Source, 3, "rows", Start.AddMilliseconds(500)), "returning to the original unrefreshed observation is never accepted");
        Check(!progress.Ready(8, Source, 4, "rows", Start.AddMilliseconds(800)), "lost query progress proof invalidates the previous stable timestamp");
        Check(progress.Ready(8, Source, 4, "rows", Start.AddMilliseconds(1550)), "valid progress can be established again after proof loss");

        progress.Submit(10, Source, 3);
        Check(!progress.Ready(10, Source, 4, "identical rows", Start), "new rows start the result revision stability window");
        Check(!progress.Ready(10, Source, 5, "identical rows", Start.AddMilliseconds(740)), "identical row rerender during stabilization resets the stable period");
        Check(!progress.Ready(10, Source, 5, "identical rows", Start.AddMilliseconds(1489)) && progress.Ready(10, Source, 5, "identical rows", Start.AddMilliseconds(1490)), "identical rows approve only after the last result revision has stayed stable for 750 milliseconds");
        Check(!progress.Ready(10, Source, 6, "identical rows", Start.AddMilliseconds(2000)) && progress.Ready(10, Source, 6, "identical rows", Start.AddMilliseconds(2750)), "a new revision invalidates even an already approved result");

        progress.Submit(10, Source, 3);
        Check(!progress.Ready(11, Source, 0, "identical rows", Start) && !progress.Ready(12, Source, 0, "identical rows", Start.AddMilliseconds(740)), "a second navigation restarts stability even with the same URL, row text and revision");
        Check(!progress.Ready(12, Source, 0, "identical rows", Start.AddMilliseconds(1489)) && progress.Ready(12, Source, 0, "identical rows", Start.AddMilliseconds(1490)), "only the last navigation's continuously stable result is approved");

        progress.Submit(20, Source, 3);
        string changedSource = Source + "?result=next";
        Check(!progress.Ready(20, Source, 4, "identical rows", Start) && !progress.Ready(20, changedSource, 4, "identical rows", Start.AddMilliseconds(740)), "source changes reset stability independently of navigation number and revision");
        Check(!progress.Ready(20, changedSource, 4, "identical rows", Start.AddMilliseconds(1489)) && progress.Ready(20, changedSource, 4, "identical rows", Start.AddMilliseconds(1490)), "same source, document, revision and fingerprint must all remain stable through the boundary");

        progress.Submit(30, Source, 0);
        bool approvedDuringRerenders = false;
        for (int resultRevision = 1; resultRevision <= 5; resultRevision++)
            approvedDuringRerenders |= progress.Ready(30, Source, resultRevision, "identical rows", Start.AddMilliseconds((resultRevision - 1) * 500));
        Check(!approvedDuringRerenders, "continuous identical-content rerenders never accumulate stability across revisions");
        Check(progress.Ready(30, Source, 5, "identical rows", Start.AddMilliseconds(2750)), "a continuously rerendering list becomes ready only after its final revision settles");
    }
}
