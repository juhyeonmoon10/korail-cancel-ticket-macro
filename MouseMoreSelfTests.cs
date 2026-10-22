using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

internal sealed partial class MainWindow
{
    // Local fixture only. These checks never start the input loop or invoke
    // a path that can send a native click, wheel input, query or reservation.
    async Task RunMouseMoreTests()
    {
        if (!Program.SelfTest) throw new InvalidOperationException("로컬 모의 검사 전용입니다.");
        int notificationsBefore = demoNotifications.Count;
        try
        {
            Assert(!MoreAppended(new[] { "a", "b" }, new[] { "a", "b" }) &&
                MoreAppended(new[] { "a", "b" }, new[] { "a", "b", "c" }),
                "more progress: only a longer unchanged identity prefix proves an append");
            ExpectMouseMoreInvalid(delegate { MoreAppended(new[] { "a", "b" }, new[] { "b", "a", "c" }); },
                "more progress: reordering existing trains is rejected");
            ExpectMouseMoreInvalid(delegate { MoreAppended(new[] { "a", "b" }, new[] { "a" }); },
                "more progress: a shortened list is rejected");
            ExpectMouseMoreInvalid(delegate { MoreAppended(new[] { "a", "b" }, new[] { "a", "c", "d" }); },
                "more progress: replacing an existing train is rejected");
            ExpectMouseMoreInvalid(delegate { MoreKeys(new Dictionary<string, object> { { "keys", new[] { "a", "a" } } }); },
                "more progress: duplicate identities are rejected before comparing progress");
            ExpectMouseMoreInvalid(delegate { MoreKeys(new Dictionary<string, object> { { "keys", "a" } }); },
                "more progress: a string cannot masquerade as an identity array");

            await PrepareMouseMoreFixture();
            string[] selectedKeys = mouseTargets.Select(target => target.Key).ToArray();
            var first = await ReadMouseMore();
            CheckMorePlan(first);
            Assert(Get(first, "state") == "ready" && MoreFlag(first, "more") && !MoreFlag(first, "allTargetsPresent") &&
                Get(ObjectValue(first, "action"), "kind") == "more" && MoreKeys(first).Length == 1,
                "more reader: a later selected train missing from the first page exposes the observed pagination action");
            mouseMoreQuery = Get(first, "queryFingerprint");
            Assert(!String.IsNullOrEmpty(mouseMoreQuery) && mouseTargets[0].Train == "KTX 513" && mouseTargets[1].Train == "KTX 505",
                "more reader: pagination freezes the original query and preserves candidate priority");
            var changedQuery = new Dictionary<string, object>(first);
            changedQuery["queryFingerprint"] = mouseMoreQuery + "changed";
            ExpectMouseMoreInvalid(delegate { CheckMorePlan(changedQuery); },
                "more reader: a changed query fingerprint cannot continue pagination");

            mouseMoreBaseline = MoreKeys(first); mouseMorePending = true; mouseMoreCount = 1;
            mouseMoreSent = DateTime.UtcNow.AddSeconds(-3); mouseMoreStableKey = null;
            DateTime originalSent = mouseMoreSent;
            int run = mouseGeneration;
            Assert(await TickMouseMore(run) && mouseMorePending && mouseMoreStableKey == null && mouseMoreCount == 1 && mouseMoreSent == originalSent,
                "more pending: an unchanged result waits without repeating the click or extending its deadline");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('tab_tab_info1').setAttribute('aria-busy','true');");
            mouseMoreStableKey = "previous-observation";
            Assert(await TickMouseMore(run) && mouseMorePending && mouseMoreStableKey == null && mouseMoreSent == originalSent,
                "more pending: loading clears stability evidence and keeps the original request deadline");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('tab_tab_info1').removeAttribute('aria-busy');");

            await RestoreMouseMoreFixtureTail();
            var expanded = await ReadMouseMore();
            CheckMorePlan(expanded);
            Assert(MoreAppended(mouseMoreBaseline, MoreKeys(expanded)) && MoreFlag(expanded, "allTargetsPresent") && MoreKeys(expanded).Length == 6,
                "more reader: appended later-page cards are visible with their complete original identities");
            Assert(await TickMouseMore(run) && mouseMorePending && mouseMoreStableKey != null,
                "more pending: an appended page requires a stability interval before it is accepted");
            Assert(await TickMouseMore(run) && mouseMorePending,
                "more pending: an immediate second observation cannot skip the one-second stability interval");
            await Task.Delay(1100);
            Assert(await TickMouseMore(run) && !mouseMorePending && mouseMoreCount == 1 && mouseMoreSent == originalSent,
                "more pending: a stable appended page completes one pending click without another input");
            Assert(!await TickMouseMore(run) && mouseTargets.Select(target => target.Key).SequenceEqual(selectedKeys),
                "more handoff: all frozen candidates present hands control back without expanding or changing their order");
            await AssertMouseMoreNoInput("more pending checks: no mouse, seat, reservation, query or notification action was emitted", notificationsBefore);

            // Simulate a WebView read finishing after an outstanding request's
            // original deadline. This local fixture cannot send any input.
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              window.__savedMoreReader=KorailMouseMore;window.__delayedMoreRead=false;
              window.KorailMouseMore={read(...args){window.__delayedMoreRead=true;
                const until=Date.now()+1500;while(Date.now()<until){};
                return window.__savedMoreReader.read(...args);}};
            })();");
            mouseMorePending = true; mouseMoreStableKey = json.Serialize(MoreKeys(expanded));
            mouseMoreStableSince = DateTime.UtcNow.AddSeconds(-2); mouseMoreSent = DateTime.UtcNow.AddSeconds(-29);
            bool delayedRejected = false;
            try { await TickMouseMore(run); }
            catch (InvalidOperationException ex) { delayedRejected = ex.Message.Contains("30초"); }
            await view.CoreWebView2.ExecuteScriptAsync("window.KorailMouseMore=window.__savedMoreReader;");
            Assert(delayedRejected && mouseMorePending && await EvalBool("window.__delayedMoreRead"),
                "more pending: an observation crossing the original deadline cannot confirm a late page");
            mouseMorePending = false;

            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('more-self-test-controls').remove();");
            Assert(!await TickMouseMore(run),
                "more handoff: all selected candidates present needs no pagination control");
            await view.CoreWebView2.ExecuteScriptAsync("window.__moreSelfTest.tail.forEach(row=>row.remove());");
            var absent = await ReadMouseMore();
            Assert(Get(absent, "state") == "ready" && !MoreFlag(absent, "more") && !MoreFlag(absent, "allTargetsPresent"),
                "more end: a missing candidate and no pagination control are distinguished from successful discovery");
            bool missingRejected = false;
            try { await TickMouseMore(run); }
            catch (InvalidOperationException ex) { missingRejected = ex.Message.Contains("선택한 열차 일부"); }
            Assert(missingRejected && !mouseMorePending && mouseMoreCount == 1,
                "more end: a selected train still absent at the end rejects the run without another click");
            await RestoreMouseMoreFixtureTail();
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              const duplicate=__cardDemo.row('505').cloneNode(true);duplicate.id='more-self-test-duplicate';
              document.querySelector('#tab_tab_info1 > .tckWrap > ul').append(duplicate);
            })();");
            var duplicatePlan = await ReadMouseMore();
            Assert(Get(duplicatePlan, "state") == "blocked",
                "more reader: duplicate rendered train identities reject a page even when every candidate exists");
            ExpectMouseMoreInvalid(delegate { CheckMorePlan(duplicatePlan); },
                "more host: a blocked duplicate result cannot enter the native input path");
            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('more-self-test-duplicate').remove();");

            mouseReadAll = true;
            Assert(await TickMouseMore(run) && !mouseBookingActive && !mouseReadAll && !mouseMorePending,
                "more full-list read: the final page scans the complete list and stops the pagination workflow");
            Assert(await EvalBool("(() => {const s=KorailDesktopHelper.snapshot();return s.candidates.length===6 && s.candidates.some(x=>x.train==='KTX 513') && !s.target && !s.confirmed;})()"),
                "more full-list read: later-page trains enter the picker without selecting or confirming a ticket");
            Assert(!sleepPrevention.Active && (mouseBookingTimer == null || !mouseBookingTimer.Enabled),
                "more local tests: no native input loop or sleep-prevention lease was started");
            await AssertMouseMoreNoInput("more completed read: no site action or notification was emitted", notificationsBefore);

            await PrepareMouseMoreFixture();
            var pausePlan = await ReadMouseMore();
            CheckMorePlan(pausePlan);
            mouseMoreQuery = Get(pausePlan, "queryFingerprint"); mouseMoreBaseline = MoreKeys(pausePlan);
            mouseMorePending = true; mouseMoreCount = 2; mouseMoreSent = DateTime.UtcNow.AddSeconds(-5);
            DateTime sentBeforePause = mouseMoreSent;
            InterruptMouseBooking("더보기 모의 입력");
            Assert(mousePaused && mouseBookingActive && mouseMorePending && mouseMoreCount == 2 && mouseMoreSent == sentBeforePause,
                "more pause: an outstanding page retains its counter and original thirty-second deadline");
            CheckMouseMoreDeadline();
            await ValidateMouseResume(mouseGeneration);
            Assert(mousePaused && mouseMorePending && mouseMoreCount == 2 && mouseMoreSent == sentBeforePause &&
                mouseTargets[0].Train == "KTX 513" && mouseTargets[1].Train == "KTX 505",
                "more resume validation: a pending page can be revalidated while a frozen later-page candidate is absent");
            mouseMoreSent = DateTime.UtcNow.AddSeconds(-31);
            DateTime expiredSent = mouseMoreSent;
            ExpectMouseMoreInvalid(delegate { CheckMouseMoreDeadline(); },
                "more pause: expiration during a pause rejects an outstanding page without retrying");
            ExpectMouseMoreInvalid(delegate { CheckMousePauseDeadline(); },
                "more pause: the ordinary pause deadline check also enforces the outstanding page deadline");
            Assert(mousePaused && mouseMorePending && mouseMoreCount == 2 && mouseMoreSent == expiredSent,
                "more pause: rejecting a timeout never restarts its deadline or resets the page count");
            mouseMorePending = false;
            mousePaused = false;
            mousePhase = "drawer";
            bool drawerRejected = false;
            try { await TickMouseMore(mouseGeneration); }
            catch (InvalidOperationException) { drawerRejected = true; }
            Assert(drawerRejected, "more stage guard: pagination is rejected after price selection");
            mousePhase = "select"; mouseSubmitted = true;
            bool submittedRejected = false;
            try { await TickMouseMore(mouseGeneration); }
            catch (InvalidOperationException) { submittedRejected = true; }
            Assert(submittedRejected, "more stage guard: pagination is rejected after reservation submission");
            mouseSubmitted = false;
            await AssertMouseMoreNoInput("more pause and stage checks: no input, reservation or notification was emitted", notificationsBefore);
        }
        finally
        {
            mouseSubmitted = false; mouseAttemptId = null;
            StopMouseBooking(null);
            ResetMouseMore();
            mouseBookingBusy = false; mouseResuming = false;
            mouseTarget = null; mouseTargets = null; mouseStore = null; mousePhase = null;
            mouseStarted = mousePhaseStarted = default(DateTime); mouseReaderNavigation = -1;
            mouseNoticeCount = mouseScrollCount = 0; mouseLastInput = 0;
            mouseRefreshCount = 0; mouseRefreshRevision = 0;
            mouseRefreshNext = mouseRefreshSent = mouseRefreshStableSince = default(DateTime);
            ShowMouseContinue();
        }
    }

    void ExpectMouseMoreInvalid(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, name);
    }

    async Task PrepareMouseMoreFixture()
    {
        StopMouseBooking(null); ResetMouseMore();
        await LoadCardDemo("available");
        await SelectCardTrain("KTX 505", "일반실");
        await ApplyMousePauseFixtureNumbers("513,505");
        string raw = await view.CoreWebView2.ExecuteScriptAsync("({selection:KorailDesktopHelper.snapshot(),query:KorailSiteAdapter.query(document)})");
        var data = json.Deserialize<Dictionary<string, object>>(raw);
        mouseTargets = BookingSelection.FromSnapshot(ObjectValue(data, "selection"), ObjectValue(data, "query"));
        mouseTarget = mouseTargets[0].Copy();
        string directory = Path.Combine(Program.DataRoot, "mouse-more-selftests");
        Directory.CreateDirectory(directory);
        mouseStore = new BookingAttemptStore(Path.Combine(directory, Guid.NewGuid().ToString("N") + "-booking-attempts.json"));
        mouseDemo = true; mouseSubmitted = mouseCompletionRecheck = false; mouseAttemptId = null;
        mousePhase = "select"; mouseReaderNavigation = -1; mouseBookingBusy = mouseResuming = false;
        mouseStarted = mousePhaseStarted = DateTime.UtcNow;
        mouseRefreshCount = 0; mouseRefreshRevision = 0; mouseNoticeCount = mouseScrollCount = 0;
        await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
          const s=document.getElementById('korail-one-helper').shadowRoot;
          s.querySelector('main').hidden=true;s.getElementById('fold').textContent='+';
          const list=document.querySelector('#tab_tab_info1 > .tckWrap > ul');
          const tail=Array.from(list.querySelectorAll(':scope > li.tckList')).slice(1);
          tail.forEach(row=>row.remove());
          const controls=document.createElement('ul');controls.className='btnList';controls.id='more-self-test-controls';
          controls.style.display='block';
          controls.innerHTML='<li><div><a href=""#none"" class=""page_group"" style=""display:block;width:160px;height:44px""><span>더보기</span></a></div></li>';
          const state=window.__moreSelfTest={tail,moreClicks:0};
          controls.querySelector('a').addEventListener('click',e=>{e.preventDefault();state.moreClicks++;});
          document.getElementById('tab_tab_info1').append(controls);window.scrollTo(0,0);
        })();");
        mouseBookingActive = true;
    }

    async Task RestoreMouseMoreFixtureTail()
    {
        await view.CoreWebView2.ExecuteScriptAsync("window.__moreSelfTest.tail.forEach(row=>document.querySelector('#tab_tab_info1 > .tckWrap > ul').append(row));");
    }

    async Task AssertMouseMoreNoInput(string name, int notificationsBefore)
    {
        Assert(demoNotifications.Count == notificationsBefore && await EvalBool(@"(() => {
          const s=__cardDemo.snapshot();return __moreSelfTest.moreClicks===0 && s.gradeClicks===0 &&
            s.reserveClicks===0 && s.filterQueries===0 && s.mainQueries===0;
        })()"), name);
    }
}
