using System;
using System.Globalization;
using System.Threading.Tasks;

internal sealed partial class MainWindow
{
    // These checks only read the local WebView fixture. Future deadlines or an
    // already submitted mock request keep every path before native input.
    async Task RunRefreshLimitTests()
    {
        if (!Program.SelfTest) throw new InvalidOperationException("로컬 모의 검사 전용입니다.");
        int savedInterval = mouseRefreshIntervalSeconds, savedLimit = mouseRefreshLimit, savedJitter = mouseRefreshJitterPercent;
        int notificationsBefore = demoNotifications.Count;
        try
        {
            mouseRefreshIntervalSeconds = 5; mouseRefreshLimit = 999999; mouseRefreshJitterPercent = 0;
            await PrepareMousePauseFixture(false, false);
            DateTime beforePrepare = DateTime.UtcNow;
            Assert(await PrepareMouseRefresh(true, mouseGeneration),
                "refresh limits: the native host accepts a five-second interval and 999999 queries");
            DateTime afterPrepare = DateTime.UtcNow;
            Assert(mouseRefreshIntervalSeconds == 5 && mouseRefreshLimit == 999999 &&
                mouseRefreshNext >= beforePrepare.AddSeconds(5) && mouseRefreshNext <= afterPrepare.AddSeconds(5),
                "refresh limits: initial scheduling retains the entered interval and count without clamping");
            DateTime clock = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
            ScheduleNextMouseRefresh(clock);
            Assert(mouseRefreshNext == clock.AddSeconds(5),
                "refresh limits: zero jitter schedules exactly five seconds rather than thirty seconds");

            mouseRefreshCount = 20; mouseStarted = DateTime.UtcNow.AddMinutes(-11);
            mouseRefreshNext = DateTime.UtcNow.AddHours(1);
            DateTime next = mouseRefreshNext;
            int run = mouseGeneration;
            await TickMouseRefresh(run);
            Assert(mouseBookingActive && mouseGeneration == run && mouseRefreshCount == 20 && mouseRefreshNext == next,
                "refresh limits: twenty completed queries do not stop a larger configured limit after eleven minutes");
            mouseRefreshCount = 999998;
            await TickMouseRefresh(run);
            Assert(mouseBookingActive && mouseRefreshCount == 999998 && mouseRefreshNext == next,
                "refresh limits: a large count below the chosen limit keeps waiting without resetting its deadline");

            InterruptMouseBooking("장시간 대기 모의 입력");
            int pausedRun = mouseGeneration;
            await TickMouseBooking();
            Assert(mouseBookingActive && mousePaused && mouseGeneration == pausedRun && mouseRefreshCount == 999998 && mouseRefreshNext == next,
                "refresh limits: a paused run remains available after eleven minutes without changing its count");
            await ValidateMouseResume(pausedRun);
            Assert(mouseBookingActive && mousePaused && mouseGeneration == pausedRun,
                "refresh limits: read-only resume validation has no hidden ten-minute run limit");

            mouseRefreshPending = true; mouseRefreshSent = DateTime.UtcNow.AddSeconds(-31);
            bool pendingRejected = false;
            try { await TickMouseRefresh(mouseGeneration); }
            catch (InvalidOperationException ex) { pendingRejected = ex.Message.Contains("30초"); }
            Assert(pendingRejected && mouseRefreshCount == 999998 && mouseRefreshPending,
                "refresh limits: an unconfirmed request still times out after thirty seconds without another query");
            bool pausedPendingRejected = false;
            try { CheckMousePauseDeadline(); }
            catch (InvalidOperationException ex) { pausedPendingRejected = ex.Message.Contains("조회 결과 확인 기한"); }
            Assert(pausedPendingRejected,
                "refresh limits: pausing never extends the original request confirmation timeout");
            mouseRefreshPending = false;

            await PrepareMousePauseFixture(false, false);
            mouseRefreshIntervalSeconds = 1; mouseRefreshLimit = Int32.MaxValue; mouseRefreshJitterPercent = 0;
            Assert(await PrepareMouseRefresh(true, mouseGeneration),
                "refresh limits: the host accepts one second and the maximum supported integer count");
            mouseRefreshNext = DateTime.UtcNow.AddHours(1); mouseRefreshCount = Int32.MaxValue - 1;
            await TickMouseRefresh(mouseGeneration);
            Assert(mouseBookingActive && mouseRefreshCount == Int32.MaxValue - 1,
                "refresh limits: a count one below Int32 maximum remains valid without integer overflow");
            mouseRefreshCount = Int32.MaxValue;
            await TickMouseRefresh(mouseGeneration);
            Assert(!mouseBookingActive && mouseRefreshCount == Int32.MaxValue && status.Text.Contains(Int32.MaxValue.ToString(CultureInfo.InvariantCulture)),
                "refresh limits: reaching the exact configured maximum stops before another increment or input");

            await PrepareMousePauseFixture(false, false);
            Assert(await PrepareMouseRefresh(true, mouseGeneration),
                "refresh limits: final-result fixture prepares with the maximum supported count");
            var plan = await ReadMouseRefresh(mouseRefreshQuery);
            CheckRefreshPlan(plan);
            mouseRefreshCount = Int32.MaxValue; mouseRefreshPending = true;
            mouseRefreshSent = DateTime.UtcNow; mouseRefreshSawChange = true;
            mouseRefreshRevision = NumberValue(plan, "revision"); mouseRefreshBaseline = Get(plan, "fingerprint");
            mouseRefreshStableKey = mouseRefreshRevision.ToString(CultureInfo.InvariantCulture) + "|" + mouseRefreshBaseline;
            mouseRefreshStableSince = DateTime.UtcNow.AddSeconds(-2);
            await TickMouseRefresh(mouseGeneration);
            Assert(!mouseBookingActive && !mouseRefreshPending && mouseRefreshCount == Int32.MaxValue && status.Text.Contains("완료"),
                "refresh limits: confirmation of the last permitted result completes without overflow or rescheduling");
            Assert(demoNotifications.Count == notificationsBefore && await EvalBool("(() => {const s=__cardDemo.snapshot();return s.gradeClicks===0 && s.reserveClicks===0 && s.filterQueries===0 && s.mainQueries===0;})()"),
                "refresh limit tests: no seat, reservation, refresh or notification is emitted");
            Assert((mouseBookingTimer == null || !mouseBookingTimer.Enabled) && !sleepPrevention.Active,
                "refresh limit tests: no native input loop or sleep-prevention session was started");
        }
        finally
        {
            mouseSubmitted = false; mouseAttemptId = null;
            StopMouseBooking(null);
            mouseBookingBusy = false; mouseResuming = false;
            mouseTarget = null; mouseTargets = null; mouseStore = null; mousePhase = null;
            mouseStarted = mousePhaseStarted = default(DateTime); mouseReaderNavigation = -1;
            mouseNoticeCount = mouseScrollCount = 0; mouseLastInput = 0;
            mouseRefreshCount = 0; mouseRefreshRevision = 0;
            mouseRefreshNext = mouseRefreshSent = mouseRefreshStableSince = default(DateTime);
            mouseRefreshIntervalSeconds = savedInterval; mouseRefreshLimit = savedLimit; mouseRefreshJitterPercent = savedJitter;
            ShowMouseContinue();
        }
    }
}
