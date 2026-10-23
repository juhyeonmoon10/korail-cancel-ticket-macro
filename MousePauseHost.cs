using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    bool mousePaused, mouseResuming;
    string mousePauseSource;
    Button mouseContinueButton;

    bool CanPauseMouseBooking()
    { return mouseBookingActive && mousePhase == "select" && !mouseSubmitted && !mouseCompletionRecheck; }

    void ShowMouseContinue()
    {
        if (mouseContinueButton != null)
            mouseContinueButton.Enabled = mousePaused && mouseBookingActive && !mouseBookingBusy && !mouseResuming && !closing;
    }

    void InterruptMouseBooking(string reason)
    {
        if (!CanPauseMouseBooking()) { StopMouseBooking(reason + " · 예약 진행 상태를 직접 확인하세요."); return; }
        if (mousePaused) return;
        mousePaused = true;
        mousePauseSource = view.CoreWebView2.Source;
        mouseGeneration++; // Invalidate any asynchronous read already in flight.
        ReleaseSleepPrevention();
        ShowMouseContinue();
        status.Text = "일시정지 · " + reason + " · 창을 앞에 두고 ‘계속’을 누르세요. 조회 횟수와 열차 선택은 유지됩니다.";
    }

    void CheckMousePauseDeadline()
    {
        CheckMouseMoreDeadline();
        if (mouseRefreshPending && (DateTime.UtcNow - mouseRefreshSent).TotalSeconds > 30)
            throw new InvalidOperationException("일시정지 중 조회 결과 확인 기한이 지났습니다. 추가 조회 없이 중지했습니다.");
    }

    async Task ValidateMouseResume(int run)
    {
        if (mousePhase != "select" || mouseSubmitted || mouseCompletionRecheck)
            throw new InvalidOperationException("가격 선택이나 예매 요청 뒤에는 자동으로 재개할 수 없습니다.");
        if (view.CoreWebView2.Source != mousePauseSource || !MouseAllowedSource(mousePauseSource))
            throw new InvalidOperationException("페이지가 바뀌었습니다. 새 조회 결과에서 다시 시작하세요.");
        CheckMousePauseDeadline();
        bool expandingMissing = false;
        if (mouseReadAll || mouseMorePending || (!mouseRefreshOnly && !mouseRefreshPending))
        {
            var more = await ReadMouseMore();
            if (run != mouseGeneration || closing || !mousePaused) return;
            if (more != null && !String.IsNullOrEmpty(Get(more, "blockCode"))) ObserveSiteBlock(Get(more, "blockCode"));
            if (run != mouseGeneration || closing || !mousePaused) return;
            CheckMorePlan(more);
            if (run != mouseGeneration || closing || !mousePaused) return;
            if (Get(more, "state") != "ready" && !(mouseMorePending && Get(more, "state") == "loading"))
                throw new InvalidOperationException("더보기 목록 상태를 확인하지 못해 재개하지 않았습니다.");
            if (mouseMorePending && Get(more, "state") == "ready") MoreAppended(mouseMoreBaseline, MoreKeys(more));
            if (mouseReadAll) { CheckMousePauseDeadline(); return; }
            expandingMissing = mouseMorePending || !MoreFlag(more, "allTargetsPresent");
            if (expandingMissing && !mouseMorePending && !MoreFlag(more, "more"))
                throw new InvalidOperationException("선택한 열차 일부와 더보기 버튼이 없어 재개하지 않았습니다.");
        }
        System.Collections.Generic.Dictionary<string, object> seatPlan = null;
        if (!mouseAutoRefresh && !expandingMissing)
        {
            seatPlan = await ReadMousePlan();
            if (run != mouseGeneration || closing || !mousePaused) return;
            if (!String.IsNullOrEmpty(Get(seatPlan, "blockCode"))) ObserveSiteBlock(Get(seatPlan, "blockCode"));
            if (run != mouseGeneration || closing || !mousePaused) return;
        }
        if (mouseAutoRefresh)
        {
            var refresh = await ReadMouseRefresh(mouseRefreshQuery);
            if (run != mouseGeneration || closing || !mousePaused) return;
            CheckRefreshPlan(refresh);
            if (Get(refresh, "state") != "ready" && !((mouseRefreshPending || mouseMorePending) && Get(refresh, "state") == "loading"))
                throw new InvalidOperationException("조회 화면 상태를 확인하지 못해 재개하지 않았습니다.");
        }
        // A manual seat selection must never be discarded by a resumed query.
        bool selected = await EvalBool(@"(() => {const s=window.KorailSiteAdapter;if(!s)return true;
          return Array.from(document.querySelectorAll(s.cardSelector+' > .tck_inner > .price_box.active,#container > .ticket_reserv_wrap')).some(e=>
            !e.closest('[hidden],[aria-hidden=true]') && getComputedStyle(e).display!=='none' && getComputedStyle(e).visibility!=='hidden' && e.getClientRects().length>0);})()");
        if (run != mouseGeneration || closing || !mousePaused) return;
        if (selected) throw new InvalidOperationException("직접 선택한 좌석 또는 예매 영역이 있습니다. 내용을 확인한 뒤 다시 시작하세요.");
        if (!mouseRefreshOnly)
        {
            string raw = await view.CoreWebView2.ExecuteScriptAsync("({selection:window.KorailDesktopHelper.snapshot(),query:window.KorailSiteAdapter.query(document)})");
            if (run != mouseGeneration || closing || !mousePaused) return;
            var data = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(raw);
            var current = queueRunning
                ? QueuedMouseResumeSelection(ObjectValue(data, "query"))
                : BookingSelection.FromSnapshot(ObjectValue(data, "selection"), ObjectValue(data, "query"));
            if (mouseTargets == null || current.Count != mouseTargets.Count || current.Where((target, i) => target.Key != mouseTargets[i].Key || target.Train != mouseTargets[i].Train).Any())
                throw new InvalidOperationException("일시정지 중 선택한 열차나 조건이 바뀌었습니다. 다시 적용하고 시작하세요.");
            if (!mouseDemo && mouseTargets.Any(target => String.CompareOrdinal(target.Date, DateTime.Today.ToString("yyyy-MM-dd")) < 0))
                throw new InvalidOperationException("승차 날짜가 지났습니다. 다시 조회하세요.");
            if (mouseTargets.Any(target => mouseStore.FindByTarget(target) != null))
                throw new InvalidOperationException("예약 시도 기록이 발견되었습니다. 예약 결과를 확인하세요.");
            // An outstanding query keeps its original count and 30-second deadline.
            // Its existing pending path must finish before any new input is possible.
            if (!mouseRefreshPending && !expandingMissing)
            {
                var plan = seatPlan ?? await ReadMousePlan();
                if (run != mouseGeneration || closing || !mousePaused) return;
                if (!String.IsNullOrEmpty(Get(plan, "blockCode"))) ObserveSiteBlock(Get(plan, "blockCode"));
                if (run != mouseGeneration || closing || !mousePaused) return;
                var action = ObjectValue(plan, "action");
                if (Get(plan, "state") == "ready" && Get(action, "kind") == "seat")
                    BookingSelection.MatchSelected(ObjectValue(plan, "selectedTarget"), mouseTargets);
                else if (plan == null || Get(plan, "state") != "waiting" || !plan.ContainsKey("refreshEligible") || !Object.Equals(plan["refreshEligible"], true))
                    throw new InvalidOperationException("기존 후보의 좌석 상태를 확인하지 못했습니다. " + Get(plan, "message"));
            }
        }
        CheckMousePauseDeadline();
    }

    System.Collections.Generic.List<BookingTarget> QueuedMouseResumeSelection(System.Collections.Generic.Dictionary<string, object> query)
    {
        if (!queueRunning || queueStage != "booking" || queueJob == null || !mouseAutoRefresh || mouseRefreshOnly || String.IsNullOrEmpty(mouseRefreshQuery))
            throw new InvalidOperationException("재개할 예약 순서와 고정 조회 조건을 확인하지 못했습니다.");
        var current = QueueStore().Get(queueJob.Id);
        if (current == null || current.State != BookingQueueState.Running || current.AttemptId != null || current.QueryJson != queueJob.QueryJson ||
            mouseTargets == null || current.Targets.Count != mouseTargets.Count ||
            current.Targets.Where((target, i) => target.Key != mouseTargets[i].Key || target.Train != mouseTargets[i].Train).Any())
            throw new InvalidOperationException("일시정지 중 예약 순서의 열차나 진행 상태가 바뀌었습니다. 예약 기록을 확인하세요.");
        // A queued candidate can still be below More, and the helper's ordinary
        // journey filter can differ from this job. Neither should erase the
        // explicit saved queue intent. Bind resume to the effective query frozen
        // at native start, including a legitimately advanced start time.
        var frozen = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(mouseRefreshQuery);
        frozen["supported"] = true;
        if (SavedTrainSelection.QueryText(query, current.Targets) != SavedTrainSelection.QueryText(frozen, current.Targets))
            throw new InvalidOperationException("일시정지 중 예약 순서의 조회 조건이 바뀌었습니다. 다시 조회하세요.");
        return current.Targets.Select(target => target.Copy()).ToList();
    }

    async Task ResumeMouseBooking()
    {
        if (!mousePaused || !mouseBookingActive || mouseBookingBusy || mouseResuming || closing) return;
        if (!AutomationAvailable()) return;
        int run = mouseGeneration;
        mouseResuming = true; ShowMouseContinue();
        try
        {
            if (NativeMouseInput.IsStopPressed()) { StopMouseBooking("F8로 중지했습니다."); return; }
            if (!NativeMouseInput.IsForeground(this) || WindowState == FormWindowState.Minimized)
            { status.Text = "일시정지 · 창을 앞에 두고 ‘계속’을 누르세요."; return; }
            uint input = NativeMouseInput.ForeignInputVersion();
            await ValidateMouseResume(run);
            if (run != mouseGeneration || closing || !mousePaused || !mouseBookingActive) return;
            if (!AutomationAvailable()) return;
            if (NativeMouseInput.IsStopPressed()) { StopMouseBooking("F8로 중지했습니다."); return; }
            if (NativeMouseInput.ForeignInputVersion() != input || !NativeMouseInput.IsForeground(this))
            { status.Text = "재개 확인 중 입력이 감지되었습니다. 일시정지를 유지합니다. 준비되면 ‘계속’을 누르세요."; return; }
            sleepPrevention.Start(); ShowSleepStatus(false);
            mouseLastInput = input;
            mousePaused = false; mousePauseSource = null; mouseGeneration++;
            status.Text = mouseReadAll ? "더보기 포함 목록 읽기를 계속합니다 · F8 중지" : "기존 조건으로 계속합니다 · 조회 " + mouseRefreshCount + "/" + mouseRefreshLimit + "회 · F8 중지";
        }
        catch (Exception ex) { if (run == mouseGeneration) StopMouseBooking("재개하지 못했습니다. " + ex.Message); }
        finally { mouseResuming = false; ShowMouseContinue(); }
    }
}
