using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

internal sealed partial class MainWindow
{
    // These tests only read the local WebView fixture and alter its mock DOM.
    // They never start the timer, install input hooks or send native input.
    async Task RunMousePauseTests()
    {
        if (!Program.SelfTest) throw new InvalidOperationException("로컬 모의 검사 전용입니다.");
        int savedInterval = mouseRefreshIntervalSeconds, savedLimit = mouseRefreshLimit, savedJitter = mouseRefreshJitterPercent;
        int notificationsBefore = demoNotifications.Count;
        try
        {
            mouseRefreshIntervalSeconds = 60; mouseRefreshLimit = 10; mouseRefreshJitterPercent = 0;
            await PrepareMousePauseFixture(true, true);
            mouseRefreshCount = 3;
            DateTime started = mouseStarted, phaseStarted = mousePhaseStarted, next = mouseRefreshNext;
            string query = mouseRefreshQuery;
            string[] keys = mouseTargets.Select(target => target.Key).ToArray();
            int beforePause = mouseGeneration;
            InterruptMouseBooking("모의 창 전환");
            Assert(mouseBookingActive && mousePaused && !mouseSubmitted && mousePhase == "select" && mouseGeneration == beforePause + 1,
                "pause: selection stays active and invalidates the previous asynchronous generation");
            Assert(mouseTargets.Select(target => target.Key).SequenceEqual(keys) && mouseTargets[0].Train == "KTX 513" && mouseTargets[1].Train == "KTX 505",
                "pause: multiple candidates retain their explicit input order");
            Assert(mouseRefreshCount == 3 && mouseRefreshNext == next && mouseRefreshQuery == query && mouseStarted == started && mousePhaseStarted == phaseStarted,
                "pause: query count, chosen refresh deadline and original run deadlines are retained");
            Assert(!sleepPrevention.Active && (mouseBookingTimer == null || !mouseBookingTimer.Enabled),
                "pause: local test starts no input loop and holds no sleep prevention");
            int pausedRun = mouseGeneration;
            string capturedSource = mousePauseSource;
            InterruptMouseBooking("두 번째 모의 입력");
            Assert(mouseGeneration == pausedRun && mousePauseSource == capturedSource && mouseRefreshNext == next,
                "pause: repeated interruption does not replace the paused run or its deadline");
            await ValidateMouseResume(pausedRun);
            Assert(mousePaused && mouseBookingActive && mouseGeneration == pausedRun && mouseRefreshCount == 3 && mouseRefreshNext == next,
                "resume validation: unchanged local selection passes without resuming or resetting counters");

            mouseRefreshPending = true;
            mouseRefreshSent = DateTime.UtcNow.AddSeconds(-5);
            DateTime originalSent = mouseRefreshSent;
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('tab_tab_info1').setAttribute('aria-busy','true');");
            await ValidateMouseResume(mouseGeneration);
            Assert(mousePaused && mouseRefreshPending && mouseRefreshCount == 3 && mouseRefreshSent == originalSent && mouseRefreshNext == next,
                "resume validation: an outstanding loading query retains its count and original thirty-second deadline");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('tab_tab_info1').removeAttribute('aria-busy');");
            mouseRefreshSent = DateTime.UtcNow.AddSeconds(-31);
            DateTime expiredSent = mouseRefreshSent;
            await ExpectMouseResumeRejected("조회 결과 확인 기한", "resume validation: expired outstanding query is rejected before another query");
            Assert(mouseRefreshCount == 3 && mouseRefreshSent == expiredSent && mouseRefreshNext == next,
                "resume validation: rejection never resets an expired query deadline or count");

            await PrepareMousePauseFixture(false, true);
            InterruptMouseBooking("모의 입력");
            await ApplyMousePauseFixtureNumbers("505,513");
            await ExpectMouseResumeRejected("선택한 열차나 조건이 바뀌", "resume validation: changing only the candidate priority order is rejected");
            Assert(mouseTargets[0].Train == "KTX 513" && mouseTargets[1].Train == "KTX 505",
                "resume validation: changed page selection cannot overwrite the frozen candidate list");

            await PrepareMousePauseFixture(false, false);
            InterruptMouseBooking("모의 입력");
            await ValidateMouseResume(mouseGeneration);
            Assert(mousePaused && mouseTargets.Count == 1 && mouseTarget.Train == "KTX 505",
                "resume validation: the existing single-train selection remains supported");
            string originalSource = mousePauseSource;
            // Simulate an original URL different from the currently loaded local
            // fixture, without navigating or requesting an external document.
            mousePauseSource = originalSource + "&previous=1";
            await ExpectMouseResumeRejected("페이지가 바뀌", "resume validation: a different original URL is rejected before reading the page");
            mousePauseSource = originalSource;
            await view.CoreWebView2.ExecuteScriptAsync("__cardDemo.row('505').querySelector('.price_box.gen').classList.add('active');");
            await ExpectMouseResumeRejected("선택 좌석", "resume validation: a manually active seat is never discarded by resumed automation");
            await view.CoreWebView2.ExecuteScriptAsync("__cardDemo.row('505').querySelector('.price_box.gen').classList.remove('active');");
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              const drawer=document.createElement('div');drawer.className='ticket_reserv_wrap';
              drawer.style.cssText='display:block;min-height:40px';drawer.textContent='로컬 검사 예매 영역';
              document.getElementById('container').append(drawer);
            })();");
            await ExpectMouseResumeRejected("선택 좌석", "resume validation: a visible reservation drawer is rejected even without an active seat");
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#container > .ticket_reserv_wrap').remove();");
            await ValidateMouseResume(mouseGeneration);
            Assert(mousePaused && mouseBookingActive, "resume validation: removing local mock conflicts restores read-only validation");

            await PrepareMousePauseFixture(false, true);
            InterruptMouseBooking("모의 입력");
            BookingAttempt secondary = mouseStore.Begin(mouseTargets[1]);
            await ExpectMouseResumeRejected("예약 시도 기록", "resume validation: a new reservation record for a secondary candidate blocks all candidates");
            Assert(mouseStore.Get(secondary.Id).State == BookingAttemptState.Prepared && mouseStore.GetAll().Count == 1,
                "resume validation: record rejection preserves existing history without a new reservation attempt");

            await PrepareMousePauseFixture(false, false);
            InterruptMouseBooking("모의 입력");
            mousePhase = "drawer";
            await ExpectMouseResumeRejected("자동으로 재개할 수 없", "resume validation: drawer phase cannot resume automatically");
            InterruptMouseBooking("가격 선택 이후 모의 입력");
            Assert(!mouseBookingActive && !mousePaused && mouseTargets == null,
                "pause: interruption after price selection stops instead of offering continue");

            await PrepareMousePauseFixture(false, false);
            BookingAttempt submitted = mouseStore.Begin(mouseTarget);
            mouseStore.MarkSubmitted(submitted.Id);
            mouseAttemptId = submitted.Id;
            InterruptMouseBooking("제출 직전 모의 입력");
            mousePhase = "submitted"; mouseSubmitted = true;
            await ExpectMouseResumeRejected("자동으로 재개할 수 없", "resume validation: submitted requests cannot resume automatically");
            InterruptMouseBooking("제출 이후 모의 입력");
            Assert(!mouseBookingActive && !mousePaused && mouseStore.Get(submitted.Id).State == BookingAttemptState.Unknown,
                "pause: interruption after submission stops and retains an unknown result without retry");

            await RunQueuedMousePauseTests();

            // Keep the terminal block-latch case last: the process must not
            // clear a confirmed block merely to make another test possible.
            await PrepareMousePauseFixture(false, false);
            InterruptMouseBooking("차단 안내 전 모의 입력");
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              const banner=document.createElement('p');banner.id='pause-test-block';
              banner.textContent='자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002';
              document.getElementById('container').prepend(banner);
            })();");
            await ValidateMouseResume(mouseGeneration);
            Assert(siteDiagnostics.SiteBlocked && siteDiagnostics.BlockCode == "-8002" && mouseRefreshSiteBlocked && !mouseBookingActive && !mousePaused,
                "resume validation: an observed block latches and stops even when automatic refresh is disabled");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('pause-test-block').remove();");
            Assert(!AutomationAvailable() && !bookingButton.Enabled && !refreshButton.Enabled,
                "resume validation: removing the mock block banner never clears the process block latch");
            Assert(demoNotifications.Count == notificationsBefore && await EvalBool("(() => {const s=__cardDemo.snapshot();return s.gradeClicks===0 && s.reserveClicks===0 && s.filterQueries===0 && s.mainQueries===0;})()"),
                "pause tests: no seat, reservation, refresh or notification is emitted");
        }
        finally
        {
            StopMouseBooking(null);
            mouseBookingBusy = false; mouseResuming = false;
            mouseSubmitted = false; mouseAttemptId = null; mouseTarget = null; mouseStore = null; mousePhase = null;
            mouseStarted = mousePhaseStarted = default(DateTime); mouseReaderNavigation = -1;
            mouseNoticeCount = mouseScrollCount = 0; mouseLastInput = 0;
            mouseRefreshCount = 0; mouseRefreshRevision = 0;
            mouseRefreshNext = mouseRefreshSent = mouseRefreshStableSince = default(DateTime);
            mouseRefreshIntervalSeconds = savedInterval; mouseRefreshLimit = savedLimit; mouseRefreshJitterPercent = savedJitter;
            ShowMouseContinue();
        }
    }

    async Task RunQueuedMousePauseTests()
    {
        if (!Program.SelfTest || queueRunning || queueInputWatching || queueJob != null)
            throw new InvalidOperationException("활성 예약 순서가 없는 로컬 모의 검사 전용입니다.");
        var previousQueueStore = queueStore;
        string previousQueueStage = queueStage;
        int previousQueueGeneration = queueGeneration;
        var previousJourney = settings.Journey;
        int notificationsBefore = demoNotifications.Count;
        try
        {
            await PrepareMouseMoreFixture();
            var originalQuery = json.Deserialize<Dictionary<string, object>>(await view.CoreWebView2.ExecuteScriptAsync("KorailSiteAdapter.query(document)"));
            string directory = Path.Combine(Program.DataRoot, "queue-pause-selftests", Guid.NewGuid().ToString("N"));
            queueStore = new BookingQueueStore(Path.Combine(directory, "queue.json"));
            var job = queueStore.Add(mouseTargets, new RefreshOptions(60, 10, 0), SavedTrainSelection.QueryText(originalQuery, mouseTargets));
            // A real queued start may move its saved 00:00 lower bound forward.
            // Freeze that effective 01:00 query, then simulate incomplete helper
            // restoration while the selected 513 card is still below More.
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('startDate').value='2026-10-22(목) 01:00';KorailDesktopHelper.reset('queue pause fixture');");
            Assert(await PrepareMouseRefresh(false, mouseGeneration), "queue pause fixture: effective query is frozen before pausing");
            queueJob = queueStore.Begin(job.Id); queueRunning = true; queueStage = "booking";
            settings.Journey = new JourneySettings { Enabled = true, DepartureStation = "부산", ArrivalStation = "대전", Date = "2026-10-25" };
            mouseRefreshCount = 3;
            InterruptMouseBooking("예약 순서의 더보기 중 모의 입력");
            Assert(await EvalBool("!KorailDesktopHelper.snapshot().confirmed && KorailDesktopHelper.snapshot().targets.length===0"),
                "queue pause fixture: helper selection is absent while the frozen later-page candidate remains queued");
            string frozenQuery = mouseRefreshQuery;
            DateTime next = mouseRefreshNext;
            await ValidateMouseResume(mouseGeneration);
            Assert(mousePaused && mouseBookingActive && queueRunning && queueStore.Get(job.Id).State == BookingQueueState.Running &&
                mouseTargets[0].Train == "KTX 513" && mouseTargets[1].Train == "KTX 505" && mouseRefreshQuery == frozenQuery && mouseRefreshCount == 3 && mouseRefreshNext == next,
                "queue resume: missing helper candidates and a different ordinary journey filter preserve the queue intent and effective query");

            var more = await ReadMouseMore();
            mouseMoreQuery = Get(more, "queryFingerprint"); mouseMoreBaseline = MoreKeys(more);
            mouseMorePending = true; mouseMoreCount = 2; mouseMoreSent = DateTime.UtcNow.AddSeconds(-5);
            DateTime sent = mouseMoreSent;
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('tab_tab_info1').setAttribute('aria-busy','true');");
            await ValidateMouseResume(mouseGeneration);
            Assert(mousePaused && mouseMorePending && mouseMoreSent == sent && mouseMoreCount == 2 && mouseRefreshCount == 3,
                "queue resume: an unfinished More request retains its deadline and count without requiring restored checkboxes");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('tab_tab_info1').removeAttribute('aria-busy');");
            mouseMorePending = false;

            foreach (string change in new[] {
                "document.getElementById('startDate').value='2026-10-23(금) 01:00';",
                "document.getElementById('labelend').value='익산';",
                "document.getElementById('startDate').value='2026-10-22(목) 02:00';" })
            {
                await view.CoreWebView2.ExecuteScriptAsync(change);
                await ExpectMouseResumeRejected("", "queue resume: a changed date, route or effective start time is rejected");
                await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('startDate').value='2026-10-22(목) 01:00';document.getElementById('labelend').value='전주';");
            }
            // Also exercise the second observation guard independently: a form
            // can change after the earlier refresh reader has already returned.
            var changedAfterRefresh = json.Deserialize<Dictionary<string, object>>(await view.CoreWebView2.ExecuteScriptAsync("KorailSiteAdapter.query(document)"));
            changedAfterRefresh["fromTime"] = "02:00";
            bool changedRejected = false;
            try { QueuedMouseResumeSelection(changedAfterRefresh); }
            catch (InvalidOperationException) { changedRejected = true; }
            Assert(changedRejected, "queue resume: the latest query snapshot cannot drift from the frozen effective query after refresh validation");
            mouseTargets.Reverse();
            await ExpectMouseResumeRejected("예약 순서의 열차", "queue resume: changing the frozen candidate order cannot authorize another candidate");
            mouseTargets.Reverse();
            queueStore.MarkNeedsReview(job.Id);
            await ExpectMouseResumeRejected("예약 순서의 열차", "queue resume: a job awaiting result review cannot resume native booking");
            queueStore.ResetAfterUserConfirmed(job.Id, true); queueJob = queueStore.Begin(job.Id);
            var attempt = mouseStore.Begin(mouseTargets[1]);
            await ExpectMouseResumeRejected("예약 시도 기록", "queue resume: any candidate with an existing attempt blocks retry before another reservation");
            queueStore.BindAttempt(job.Id, attempt);
            await ExpectMouseResumeRejected("예약 순서의 열차", "queue resume: a queue item bound to an attempt cannot re-enter seat selection");
            Assert(mouseStore.GetAll().Count == 1 && mouseStore.Get(attempt.Id).State == BookingAttemptState.Prepared,
                "queue resume: rejected validation neither submits nor creates a second booking attempt");
            await AssertMouseMoreNoInput("queue pause tests: no native input, seat selection, reservation, query or notification was emitted", notificationsBefore);

            queueRunning = false; queueJob = null; queueStage = null;
            settings.Journey = previousJourney;
            await PrepareMousePauseFixture(true, false);
            InterruptMouseBooking("일반 예약의 확인 해제 모의 입력");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('korail-one-helper').shadowRoot.getElementById('confirmed').checked=false;");
            await ExpectMouseResumeRejected("여정 확인란", "ordinary resume: an unchecked helper selection still requires explicit confirmation");
        }
        finally
        {
            // Only isolated synthetic records were used. Clear the synthetic
            // queue before native cleanup so no real queue store can be touched.
            queueRunning = false; queueJob = null; queueStage = null;
            StopMouseBooking(null);
            queueStore = previousQueueStore; queueStage = previousQueueStage; queueGeneration = previousQueueGeneration;
            settings.Journey = previousJourney;
        }
    }

    async Task PrepareMousePauseFixture(bool refresh, bool multiple)
    {
        if (!Program.SelfTest) throw new InvalidOperationException("로컬 모의 검사 전용입니다.");
        StopMouseBooking(null);
        await LoadCardDemo("available");
        await SelectCardTrain("KTX 505", "일반실");
        if (multiple) await ApplyMousePauseFixtureNumbers("513,505");
        string raw = await view.CoreWebView2.ExecuteScriptAsync("({selection:window.KorailDesktopHelper.snapshot(),query:window.KorailSiteAdapter.query(document)})");
        var data = json.Deserialize<Dictionary<string, object>>(raw);
        mouseTargets = BookingSelection.FromSnapshot(ObjectValue(data, "selection"), ObjectValue(data, "query"));
        mouseTarget = mouseTargets[0].Copy();
        string directory = Path.Combine(Program.DataRoot, "mouse-pause-selftests");
        Directory.CreateDirectory(directory);
        mouseStore = new BookingAttemptStore(Path.Combine(directory, Guid.NewGuid().ToString("N") + "-booking-attempts.json"));
        mouseDemo = true; mouseSubmitted = false; mouseCompletionRecheck = false;
        mouseAttemptId = null; mousePhase = "select"; mouseBookingBusy = false; mouseResuming = false;
        mouseReaderNavigation = -1; mouseNoticeCount = mouseScrollCount = 0; mouseLastInput = 0;
        mouseRefreshCount = 0; mouseRefreshRevision = 0;
        mouseRefreshNext = mouseRefreshSent = mouseRefreshStableSince = default(DateTime);
        mouseStarted = mousePhaseStarted = DateTime.UtcNow;
        await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
          const s=document.getElementById('korail-one-helper').shadowRoot;
          s.querySelector('main').hidden=true;s.getElementById('fold').textContent='+';
          window.scrollTo(0,0);
        })();");
        if (refresh) Assert(await PrepareMouseRefresh(false, mouseGeneration), "pause fixture: normal read-only refresh preparation succeeds");
        mouseBookingActive = true;
    }

    async Task ApplyMousePauseFixtureNumbers(string numbers)
    {
        if (!Program.SelfTest || !view.CoreWebView2.Source.StartsWith(LocalBase + "card-demo.html", StringComparison.Ordinal))
            throw new InvalidOperationException("로컬 카드 모의 화면 전용 검사입니다.");
        await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
          const s=document.getElementById('korail-one-helper').shadowRoot;
          s.getElementById('trainNumbers').value=" + json.Serialize(numbers) + @";
          s.getElementById('trainNumbers').dispatchEvent(new Event('input'));
          s.getElementById('applyTrain').click();s.getElementById('confirmed').checked=true;
        })();");
    }

    async Task ExpectMouseResumeRejected(string messagePart, string name)
    {
        bool rejected = false;
        try { await ValidateMouseResume(mouseGeneration); }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains(messagePart); }
        Assert(rejected && mousePaused && mouseBookingActive, name);
    }
}
