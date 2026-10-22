using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

// Mouse input is limited to verified controls. Refresh is opt-in and stops on any block.
// This workflow never pays, logs in, or dismisses an unknown dialog.
internal sealed partial class MainWindow
{
    System.Windows.Forms.Timer mouseBookingTimer;
    bool mouseBookingActive, mouseBookingBusy, mouseSubmitted, mouseDemo, mouseCompletionRecheck;
    int mouseGeneration, mouseReaderNavigation = -1, mouseNoticeCount, mouseScrollCount;
    uint mouseLastInput;
    string mousePhase, mouseAttemptId;
    BookingTarget mouseTarget;
    List<BookingTarget> mouseTargets;
    BookingAttemptStore mouseStore;
    DateTime mouseStarted, mousePhaseStarted;

    bool MouseAllowedSource(string source)
    {
        Uri uri;
        if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || uri.Scheme != "https" || !uri.IsDefaultPort) return false;
        if (mouseDemo) return uri.Host == "korail-helper.invalid" && uri.AbsolutePath == "/card-demo.html";
        if (mouseCompletionRecheck) return OfficialReservationDetailSource(source);
        return (uri.Host == "www.korail.com" || uri.Host == "korail.com") && uri.AbsolutePath.StartsWith("/ticket/", StringComparison.Ordinal);
    }
    static bool OfficialReservationDetailSource(string source)
    {
        Uri uri;
        return Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort && String.IsNullOrEmpty(uri.UserInfo)
            && (uri.Host == "www.korail.com" || uri.Host == "korail.com") && uri.AbsolutePath == "/ticket/reservation/detail";
    }
    async Task EnsureMouseReader()
    {
        if (Program.ManualDiagnostic) throw new InvalidOperationException("수동 진단 모드에서는 화면 분석 스크립트를 삽입하지 않습니다.");
        if (!MouseAllowedSource(view.CoreWebView2.Source)) throw new InvalidOperationException("예약을 확인할 공식 페이지를 벗어났습니다. 직접 결과를 확인하세요.");
        if (mouseReaderNavigation == navigation && await EvalBool("!!window.KorailMouseBooking")) return;
        int page = navigation;
        await view.CoreWebView2.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BundleRoot, "core.js")) + "\n" +
            File.ReadAllText(Path.Combine(Program.BundleRoot, "korail-site.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "mouse-booking.js")));
        if (page == navigation) mouseReaderNavigation = page;
    }
    static object MouseTargetData(BookingTarget target)
    {
        return new { date = target.Date, departureStation = target.DepartureStation, arrivalStation = target.ArrivalStation,
            train = target.Train, departure = target.Departure, arrival = target.Arrival, grade = target.Grade, passengers = target.Passengers };
    }
    async Task<Dictionary<string, object>> ReadMousePlan()
    {
        int run = mouseGeneration, page = navigation;
        string source = view.CoreWebView2.Source;
        if (!MouseAllowedSource(source)) throw new InvalidOperationException("예약을 확인할 공식 페이지를 벗어났습니다. 직접 결과를 확인하세요.");
        // A submitted request may replace its document. Waiting never authorizes
        // another input, and the caller retains the original 30-second deadline.
        if (mouseSubmitted && navigationPending) return null;
        try
        {
            await EnsureMouseReader();
            if (run != mouseGeneration || closing) return null;
            if (!MouseAllowedSource(view.CoreWebView2.Source)) throw new InvalidOperationException("예약을 확인할 공식 페이지를 벗어났습니다. 직접 결과를 확인하세요.");
            if (MouseDocumentChanged(page, source)) return null;
            string read = mousePhase == "select"
                ? "window.KorailMouseBooking.readMany(" + json.Serialize(mouseTargets.Select(MouseTargetData).ToArray()) + ")"
                : "window.KorailMouseBooking.read(" + json.Serialize(MouseTargetData(mouseTarget)) + "," + json.Serialize(mousePhase) + ")";
            var raw = await view.CoreWebView2.ExecuteScriptAsync("window.KorailMouseBooking && " + read);
            if (run != mouseGeneration || closing) return null;
            if (!MouseAllowedSource(view.CoreWebView2.Source)) throw new InvalidOperationException("예약을 확인할 공식 페이지를 벗어났습니다. 직접 결과를 확인하세요.");
            if (MouseDocumentChanged(page, source) || raw == null || raw == "null") return null;
            return json.Deserialize<Dictionary<string, object>>(raw);
        }
        catch (COMException) { if (!MouseReadInterrupted(run, page, source)) throw; return null; }
        catch (InvalidOperationException) { if (!MouseReadInterrupted(run, page, source)) throw; return null; }
    }
    bool MouseDocumentChanged(int page, string source)
    { return navigationPending || page != navigation || !String.Equals(source, view.CoreWebView2.Source, StringComparison.Ordinal); }
    bool MouseReadInterrupted(int run, int page, string source)
    {
        if (run != mouseGeneration || closing) return true;
        // Only a demonstrated navigation race after submission is retryable.
        // Stable-page failures, malformed data and nonofficial URLs still stop.
        return mouseSubmitted && MouseAllowedSource(view.CoreWebView2.Source) && MouseDocumentChanged(page, source);
    }
    static Dictionary<string, object> ObjectValue(Dictionary<string, object> value, string name)
    { object item; return value != null && value.TryGetValue(name, out item) ? item as Dictionary<string, object> : null; }
    static double NumberValue(Dictionary<string, object> value, string name)
    {
        object item; double result;
        if (value == null || !value.TryGetValue(name, out item) || !Double.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result) || Double.IsNaN(result) || Double.IsInfinity(result))
            throw new InvalidOperationException("화면 좌표를 확인하지 못했습니다.");
        return result;
    }
    async Task StartMouseBooking(bool enableRefresh = false, BookingQueueJob queuedJob = null)
    {
        if (!ready || closing) return;
        if (!AutomationAvailable()) return;
        Task stopping = StopHelper("선택한 열차의 마우스 예약을 준비합니다.", queuedJob != null);
        int startRun = mouseGeneration;
        await stopping;
        if (startRun != mouseGeneration || closing) return;
        try
        {
            mouseCompletionRecheck = false;
            mouseDemo = view.CoreWebView2.Source.StartsWith(LocalBase, StringComparison.Ordinal);
            if (!MouseAllowedSource(view.CoreWebView2.Source)) throw new InvalidOperationException("코레일에서 직접 조회하고 도우미에서 열차를 선택하세요.");
            string raw = await view.CoreWebView2.ExecuteScriptAsync("(() => {const h=window.KorailDesktopHelper;const s=window.KorailSiteAdapter;if(!h||!s)return null;return {selection:h.snapshot(),query:s.query(document)};})()");
            if (startRun != mouseGeneration || closing) return;
            var data = json.Deserialize<Dictionary<string, object>>(raw);
            var selection = ObjectValue(data, "selection"); var query = ObjectValue(data, "query");
            if (queuedJob == null) mouseTargets = BookingSelection.FromSnapshot(selection, query);
            else
            {
                if (!queueRunning || queueJob == null || queueJob.Id != queuedJob.Id || mouseDemo ||
                    SavedTrainSelection.QueryText(query, queuedJob.Targets) != queuedJob.QueryJson)
                    throw new InvalidOperationException("예약 순서의 저장 조건과 실제 조회 화면이 다릅니다.");
                mouseTargets = queuedJob.Targets.Select(target => target.Copy()).ToList();
            }
            mouseTarget = mouseTargets[0].Copy();
            if (!mouseDemo && String.CompareOrdinal(mouseTarget.Date, DateTime.Today.ToString("yyyy-MM-dd")) < 0) throw new InvalidOperationException("과거 날짜는 예약할 수 없습니다. 다시 조회하세요.");
            if (!mouseDemo && queuedJob == null && settings.Journey.Enabled)
            {
                var j = settings.Journey;
                var allowedNumbers = TrainNumberSelection.Parse(j.TrainNumber);
                if (mouseTargets.Any(target => j.Date != target.Date || j.DepartureStation.Trim().TrimEnd('역') != target.DepartureStation.TrimEnd('역') ||
                    j.ArrivalStation.Trim().TrimEnd('역') != target.ArrivalStation.TrimEnd('역') || j.Grade != target.Grade ||
                    String.CompareOrdinal(target.Departure, j.FromTime) < 0 || String.CompareOrdinal(target.Departure, j.ToTime) > 0 ||
                    (allowedNumbers.Count > 0 && !allowedNumbers.Contains(Int32.Parse(Regex.Match(target.Train, @"(\d{1,5})\s*$").Groups[1].Value, CultureInfo.InvariantCulture)))))
                    throw new InvalidOperationException("선택한 열차가 저장한 날짜·구간·열차·등급과 다릅니다.");
            }
            if (!mouseDemo && (!settings.Telegram.Enabled || settings.Telegram.Validate() != null))
                throw new InvalidOperationException("먼저 텔레그램 설정에서 본인의 봇 토큰과 Chat ID를 저장하고 알림을 켜세요.");
            mouseStore = new BookingAttemptStore(Path.Combine(Program.DataRoot, mouseDemo ? "demo-booking-attempts.json" : "booking-attempts.json"));
            if (mouseTargets.Any(target => mouseStore.FindByTarget(target) != null)) throw new InvalidOperationException("선택 목록 중 예약 시도 기록이 있는 열차가 있습니다. ‘예약 기록’에서 실제 결과를 확인하세요. 다른 열차로 바꿔 자동 예매하지 않습니다.");
            mouseAttemptId = null; mouseSubmitted = false; mousePhase = "select"; mouseNoticeCount = 0; mouseScrollCount = 0;
            mouseReaderNavigation = -1; mouseStarted = mousePhaseStarted = DateTime.UtcNow;
            await EnsureMouseReader();
            if (startRun != mouseGeneration || closing) return;
            if (enableRefresh && !await PrepareMouseRefresh(false, startRun)) return;
            if (startRun != mouseGeneration || closing) return;
            if (!NativeMouseInput.IsForeground(this)) throw new InvalidOperationException("이 프로그램 창을 맨 앞으로 표시한 후 시작하세요.");
            // Fold the helper only; the official page's controls are never activated through JavaScript.
            await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.getElementById('korail-one-helper')?.shadowRoot;const m=r?.querySelector('main');if(m){m.hidden=true;r.getElementById('fold').textContent='+';}})()");
            if (startRun != mouseGeneration || closing) return;
            if (!AutomationAvailable()) return;
            siteDiagnostics.AddAction("start");
            StartMouseLoop();
            status.Text = (mouseDemo ? "[모의 연습] " : "") + mouseTarget.Date + " · " + String.Join(", ", mouseTargets.Select(target => target.Train)) + " · " + mouseTarget.Grade + " 중 한 편 확인 · F8 중지";
        }
        catch (Exception ex) { if (startRun == mouseGeneration) StopMouseBooking("시작하지 못했습니다. " + ex.Message); }
    }
    async Task StartMouseCompletionCheck(string attemptId)
    {
        if (!ready || closing || !AutomationAvailable()) return;
        Task stopping = StopHelper("기존 예약 시도의 현재 화면에서 완료 여부를 확인합니다. 추가 입력은 하지 않습니다.");
        int startRun = mouseGeneration;
        await stopping;
        if (startRun != mouseGeneration || closing) return;
        try
        {
            if (!OfficialReservationDetailSource(view.CoreWebView2.Source)) throw new InvalidOperationException("코레일의 해당 예약 상세 화면을 직접 연 뒤 다시 확인하세요.");
            if (!settings.Telegram.Enabled || settings.Telegram.Validate() != null) throw new InvalidOperationException("먼저 텔레그램 설정에서 본인의 봇 토큰과 Chat ID를 저장하고 알림을 켜세요.");
            var store = new BookingAttemptStore(Path.Combine(Program.DataRoot, "booking-attempts.json"));
            var attempt = store.Get(attemptId);
            if (attempt == null || (attempt.State != BookingAttemptState.Submitted && attempt.State != BookingAttemptState.Unknown) || attempt.SubmittedUtc == null)
                throw new InvalidOperationException("실제로 제출한 뒤 결과가 확인되지 않은 예약 기록만 다시 확인할 수 있습니다.");
            if (!NativeMouseInput.IsForeground(this)) throw new InvalidOperationException("이 프로그램 창을 맨 앞으로 표시한 후 확인하세요.");
            mouseDemo = false; mouseCompletionRecheck = true;
            mouseStore = store; mouseAttemptId = attempt.Id; mouseTarget = attempt.Target.Copy(); mouseTargets = null;
            mouseSubmitted = true; mousePhase = "submitted"; mouseNoticeCount = 0; mouseScrollCount = 0;
            mouseReaderNavigation = -1; mouseStarted = mousePhaseStarted = DateTime.UtcNow;
            await EnsureMouseReader();
            if (startRun != mouseGeneration || closing) return;
            if (!AutomationAvailable()) return;
            siteDiagnostics.AddAction("completion-check");
            StartMouseLoop();
            status.Text = "기존 예약 시도의 완료 여부만 확인 중 · 추가 예매·안내 닫기 없음 · F8로 중지";
        }
        catch (Exception ex) { if (startRun == mouseGeneration) StopMouseBooking("완료 확인을 시작하지 못했습니다. " + ex.Message); }
    }
    void StopMouseBooking(string reason)
    {
        ReleaseSleepPrevention();
        bool wasActive = mouseBookingActive;
        if (mouseAutoRefresh) RefreshLog("stop", "count=" + mouseRefreshCount + (reason == null ? "" : ", " + reason));
        mouseBookingActive = false; mouseGeneration++;
        mousePaused = false; mousePauseSource = null; ShowMouseContinue();
        ResetMouseRefresh();
        ResetMouseMore();
        try { NativeMouseInput.EndInputWatch(); }
        catch { reason = "입력 감시를 정리하지 못해 기능을 중지했습니다. 프로그램을 다시 실행하세요."; }
        if (mouseBookingTimer != null) mouseBookingTimer.Stop();
        if (wasActive && mouseSubmitted && mouseAttemptId != null && mouseStore != null)
        {
            try { mouseStore.RecordUnknown(mouseAttemptId, reason ?? "앱 중지 또는 종료. 공식 화면에서 예약 결과 확인 필요."); } catch { }
        }
        mouseCompletionRecheck = false;
        mouseTargets = null;
        QueueBookingStopped(reason);
        if (!closing && reason != null) status.Text = reason;
        if (!closing) UpdateShellState();
    }
    async Task TickMouseBooking()
    {
        if (!mouseBookingActive || mouseBookingBusy || closing) return;
        if (!AutomationAvailable()) { StopMouseBooking(status.Text); return; }
        mouseBookingBusy = true; int run = mouseGeneration;
        try
        {
            if (NativeMouseInput.IsStopPressed()) throw new InvalidOperationException("F8로 중지했습니다.");
            if (mousePaused) { CheckMousePauseDeadline(); return; }
            if (WindowState == FormWindowState.Minimized || !NativeMouseInput.IsForeground(this)) { InterruptMouseBooking("창 전환 또는 최소화"); return; }
            if (NativeMouseInput.ForeignInputVersion() != mouseLastInput) { InterruptMouseBooking("클릭·스크롤 또는 키 입력 감지"); return; }
            if (mouseReadAll || mouseMorePending) { await TickMouseMore(run); return; }
            if (mouseRefreshOnly || mouseRefreshPending)
            {
                await TickMouseRefresh(run);
                return;
            }
            if (mousePhase == "select" && await TickMouseMore(run)) return;
            if (mousePhase != "select" && (DateTime.UtcNow - mousePhaseStarted).TotalSeconds > 30)
                throw new InvalidOperationException(mouseSubmitted ? "예약 결과를 확인하지 못했습니다. 예매를 다시 누르지 말고 공식 예약내역을 확인하세요." : "30초 안에 다음 예약 단계를 확인하지 못했습니다.");
            var plan = await ReadMousePlan();
            if (run != mouseGeneration || !mouseBookingActive) return;
            if (plan == null)
            {
                if (!mouseSubmitted) throw new InvalidOperationException("화면이 바뀌어 예약 단계를 확인하지 못했습니다. 직접 결과를 확인하세요.");
                status.Text = "예매 1회 요청됨 · 예약 결과 페이지를 기다리는 중…";
                return;
            }
            string state = Get(plan, "state"), message = Get(plan, "message");
            if (!String.IsNullOrEmpty(Get(plan, "blockCode"))) ObserveSiteBlock(Get(plan, "blockCode"));
            if (state == "blocked") throw new InvalidOperationException(message);
            var action = ObjectValue(plan, "action");
            if (mouseCompletionRecheck && action != null) throw new InvalidOperationException("완료 확인은 화면 읽기만 수행합니다. 안내문을 직접 닫고 예약 상세를 표시한 뒤 ‘현재 화면에서 완료 확인’을 다시 누르세요.");
            if (state == "completed")
            {
                if (!mouseSubmitted || mouseAttemptId == null) throw new InvalidOperationException("이번 시도에 연결할 예약 완료 근거가 없습니다.");
                var result = ObjectValue(plan, "receipt");
                var observed = ObjectValue(result, "observedTarget");
                if (observed == null) throw new InvalidOperationException("예약 상세에서 여정을 읽지 못했습니다.");
                var observedTarget = new BookingTarget { Date = Get(observed, "date"), DepartureStation = Get(observed, "departureStation"), ArrivalStation = Get(observed, "arrivalStation"),
                    Train = Get(observed, "train"), Departure = Get(observed, "departure"), Arrival = Get(observed, "arrival"), Grade = Get(observed, "grade"), Passengers = (int)NumberValue(observed, "passengers") };
                string evidenceType = Get(result, "evidenceType"), seat = Get(result, "seat");
                var receipt = BookingReceipt.CreateConfirmed(mouseTarget, observedTarget, Get(result, "reservationNumber"), Get(result, "paymentDeadline"), String.IsNullOrEmpty(evidenceType) ? null : evidenceType, String.IsNullOrEmpty(seat) ? null : seat);
                var completedStore = mouseStore; string completedId = mouseAttemptId; var completedTarget = mouseTarget.Copy(); var completedTelegram = settings.Telegram.Copy();
                completedStore.RecordConfirmed(completedId, receipt);
                int completedQueueGeneration = ConfirmQueuedBooking(completedStore.Get(completedId));
                // Disarm before network I/O. A Telegram failure can never trigger another booking.
                mouseSubmitted = false; StopMouseBooking(null);
                status.Text = "예약 완료 확인 · 미결제 · 공식 화면에 표시된 기한까지 직접 결제하세요.";
                if (mouseDemo) { notificationStatus.Text = "모의 예약 완료 확인 · 실제 텔레그램 전송 없음"; return; }
                try
                {
                    WatchQueueNotification(completedQueueGeneration);
                    if (!completedStore.BeginNotification(completedId)) { notificationStatus.Text = "이 예약의 알림 전송 이력이 있습니다. 중복 전송하지 않았습니다."; ContinueQueueAfterNotification(completedQueueGeneration); return; }
                    notificationStatus.Text = "예약 완료 확인 · 텔레그램 전송 중…";
                    string sent = await TelegramNotifier.SendAsync(completedTelegram, receipt.BuildTelegramMessage(completedTarget));
                    bool success = sent == TelegramNotifier.SuccessMessage;
                    completedStore.CompleteNotification(completedId, success, success ? "" : sent);
                    if (!closing) notificationStatus.Text = success ? "예약 완료 · 텔레그램 알림 전송 완료" : "예약은 확인됐지만 알림 전송을 확인하지 못했습니다. " + sent;
                    ContinueQueueAfterNotification(completedQueueGeneration);
                }
                catch
                {
                    if (!closing) notificationStatus.Text = "예약은 확인됐지만 알림 처리를 마치지 못했습니다. 실제 Telegram 대화를 확인하세요.";
                    if (completedQueueGeneration == queueGeneration) StopBookingQueue("알림 처리 오류로 다음 예약을 시작하지 않았습니다.");
                }
                return;
            }
            if (state != "ready" || action == null)
            {
                if (mouseAutoRefresh && mousePhase == "select")
                {
                    if (!Object.Equals(plan.ContainsKey("refreshEligible") ? plan["refreshEligible"] : null, true))
                        throw new InvalidOperationException("선택 열차의 명시적인 매진 상태를 확인하지 못해 자동 재조회를 중지했습니다. " + message);
                    await TickMouseRefresh(run);
                    return;
                }
                status.Text = mouseSubmitted ? "예매 1회 요청됨 · 실제 예약 결과 확인 중…" : (String.IsNullOrWhiteSpace(message) ? "현재 표시된 좌석을 확인 중 · 자동 재조회 없음" : message + " · 자동 재조회 없음");
                return;
            }
            string kind = Get(action, "kind");
            if (kind == "receipt-notice")
            {
                if (!mouseSubmitted || mousePhase != "submitted") throw new InvalidOperationException("예매 제출 뒤의 예약 완료 안내만 닫을 수 있습니다.");
            }
            else if (mouseSubmitted) throw new InvalidOperationException("예매 요청 후 추가 입력을 중지했습니다.");
            if (kind != "seat" && kind != "notice" && kind != "reserve" && kind != "receipt-notice") throw new InvalidOperationException("알 수 없는 예약 단계입니다.");
            if (kind == "seat" && mousePhase != "select") throw new InvalidOperationException("좌석 선택이 취소되었거나 상태가 바뀌었습니다.");
            BookingTarget candidate = kind == "seat" ? BookingSelection.MatchSelected(ObjectValue(plan, "selectedTarget"), mouseTargets) : null;
            double x = NumberValue(action, "x"), y = NumberValue(action, "y"), width = NumberValue(action, "viewportWidth"), height = NumberValue(action, "viewportHeight");
            if (width <= 0 || height <= 0) throw new InvalidOperationException("브라우저 크기를 확인하지 못했습니다.");
            if (x < 0 || x >= width) throw new InvalidOperationException("버튼이 가로 화면 밖에 있습니다. 창 크기를 조정하세요.");
            // The reserve button can be fixed near the bottom of the page.
            // Native hit testing still rejects a control covered by another window.
            const double inputMargin = 8;
            if (y < inputMargin || y >= height - inputMargin)
            {
                if (mouseCompletionRecheck || (mouseSubmitted && kind != "receipt-notice")) throw new InvalidOperationException("예약 후 확인되지 않은 화면 이동을 중지했습니다. 안내문을 직접 확인하세요.");
                if (kind == "receipt-notice")
                {
                    // Only the recognized post-submission receipt notice may be
                    // scrolled. Re-read before the wheel input; do not extend the
                    // original submitted-phase deadline while revealing its button.
                    Point scrollOrigin = view.PointToScreen(Point.Empty); Size scrollViewport = view.ClientSize;
                    var scrollPlan = await ReadMousePlan(); var scrollAction = ObjectValue(scrollPlan, "action");
                    if (run != mouseGeneration || !mouseBookingActive) return;
                    if (!mouseSubmitted || mousePhase != "submitted" || mouseCompletionRecheck) throw new InvalidOperationException("예약 후 안내 확인 상태가 바뀌어 스크롤을 중지했습니다.");
                    if (scrollAction == null || Get(scrollPlan, "state") != "ready" || Get(scrollAction, "kind") != "receipt-notice"
                        || Math.Abs(NumberValue(scrollAction, "x") - x) > 1 || Math.Abs(NumberValue(scrollAction, "y") - y) > 1
                        || NumberValue(scrollAction, "viewportWidth") != width || NumberValue(scrollAction, "viewportHeight") != height
                        || scrollOrigin != view.PointToScreen(Point.Empty) || scrollViewport != view.ClientSize) return;
                    if ((DateTime.UtcNow - mousePhaseStarted).TotalSeconds > 30) throw new InvalidOperationException("30초 확인 기한이 지나 안내 스크롤을 중지했습니다. 공식 예약 결과를 직접 확인하세요.");
                }
                if (NativeMouseInput.ForeignInputVersion() != mouseLastInput) { InterruptMouseBooking("스크롤 전 직접 입력 감지"); return; }
                if (WindowState == FormWindowState.Minimized || !NativeMouseInput.IsForeground(this)) { InterruptMouseBooking("스크롤 전 창 전환"); return; }
                // Expanded result lists can be much taller than the first page.
                if (++mouseScrollCount > (mousePhase == "select" ? 100 : 8)) throw new InvalidOperationException("버튼을 화면에 표시하지 못했습니다.");
                Point center = view.PointToScreen(new Point(view.ClientSize.Width / 2, view.ClientSize.Height / 2));
                if (kind == "receipt-notice") siteDiagnostics.AddAction("native-receipt-scroll");
                NativeMouseInput.Wheel(this, center, y < inputMargin ? 360 : -360);
                return;
            }
            // Re-read immediately before input, rejecting rerenders and displaced controls.
            Point origin = view.PointToScreen(Point.Empty); Size viewport = view.ClientSize;
            var fresh = await ReadMousePlan(); var point = ObjectValue(fresh, "action");
            if (run != mouseGeneration || !mouseBookingActive) return;
            if (point == null || Get(fresh, "state") != "ready" || Get(point, "kind") != kind ||
                Math.Abs(NumberValue(point, "x") - x) > 1 || Math.Abs(NumberValue(point, "y") - y) > 1 ||
                NumberValue(point, "viewportWidth") != width || NumberValue(point, "viewportHeight") != height ||
                origin != view.PointToScreen(Point.Empty) || viewport != view.ClientSize)
                return;
            if (kind == "seat" && BookingSelection.MatchSelected(ObjectValue(fresh, "selectedTarget"), mouseTargets).Key != candidate.Key)
                return;
            if (NativeMouseInput.ForeignInputVersion() != mouseLastInput) { InterruptMouseBooking("클릭 전 직접 입력 감지"); return; }
            if (WindowState == FormWindowState.Minimized || !NativeMouseInput.IsForeground(this)) { InterruptMouseBooking("클릭 전 창 전환"); return; }
            if (mousePhase != "select" && (DateTime.UtcNow - mousePhaseStarted).TotalSeconds > 30) throw new InvalidOperationException("30초 확인 기한이 지나 추가 입력을 중지했습니다. 공식 예약 결과를 직접 확인하세요.");
            var screen = view.PointToScreen(new Point((int)Math.Round(x * view.ClientSize.Width / width), (int)Math.Round(y * view.ClientSize.Height / height)));
            if (kind == "reserve")
            {
                var attempt = mouseStore.Begin(mouseTarget); mouseAttemptId = attempt.Id;
                BindQueuedAttempt(attempt);
                mouseStore.MarkSubmitted(mouseAttemptId); mouseSubmitted = true; mousePhase = "submitted"; mousePhaseStarted = DateTime.UtcNow;
                status.Text = "선택한 열차의 예매 버튼을 한 번 누릅니다. 결과 확인 중 재시도하지 않습니다.";
            }
            else if (kind == "seat")
            {
                // Freeze one alternative before the first real seat click.
                // All following notices, submission and receipt checks use it.
                mouseTarget = candidate.Copy();
                ResetMouseRefresh(); mousePhase = "drawer"; mousePhaseStarted = DateTime.UtcNow;
                status.Text = mouseTarget.Train + " · " + mouseTarget.Grade + " 가격 선택 · 이 열차 한 편만 진행";
            }
            else if (++mouseNoticeCount > 3) throw new InvalidOperationException("예약 안내가 반복되어 중지했습니다.");
            if (!AutomationAvailable()) { StopMouseBooking(status.Text); return; }
            siteDiagnostics.AddAction(kind == "seat" ? "native-seat" : kind == "notice" ? "native-notice" : kind == "receipt-notice" ? "native-receipt-notice" : "native-reserve");
            NativeMouseInput.Click(this, screen);
            await Task.Delay(400);
        }
        catch (Exception ex) { if (run == mouseGeneration) StopMouseBooking(ex.Message); }
        finally { mouseBookingBusy = false; ShowMouseContinue(); }
    }

    void MouseScriptDialog(object sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        if (OfficialTicketSource(e.Uri)) ObserveSiteBlock(e.Message);
        if (queueRunning && queueStage == "query") StopBookingQueue("조회 중 안내창이 열렸습니다. 내용을 직접 확인하세요.");
        // Always preserve a visible dialog for the user, including when the workflow is inactive.
        bool automatic = mouseBookingActive && !mouseSubmitted && mousePhase == "drawer" &&
            e.Kind == CoreWebView2ScriptDialogKind.Alert && e.Uri == view.CoreWebView2.Source && AllowedStopNotice(e.Message);
        if (mouseBookingActive && !automatic) StopMouseBooking("안내문을 직접 확인해야 하므로 중지했습니다.");
        int run = mouseGeneration;
        var deferral = e.GetDeferral();
        try
        {
            if (automatic && ++mouseNoticeCount > 3) { automatic = false; StopMouseBooking("정차 안내가 반복되어 중지했습니다."); }
            using (var dialog = new Form { Text = automatic ? "코레일 정차 안내" : "페이지 안내 · 직접 확인", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(540, 240), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font })
            {
                var label = new Label { Text = e.Message, Dock = DockStyle.Fill, Padding = new Padding(24), TextAlign = ContentAlignment.MiddleLeft };
                var ok = new Button { Text = "확인", Dock = DockStyle.Bottom, Height = 44, DialogResult = DialogResult.OK };
                TextBox prompt = null;
                bool acknowledged = false;
                ok.Click += delegate { acknowledged = true; };
                dialog.Controls.Add(label); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
                dialog.KeyPreview = true;
                dialog.KeyDown += delegate(object s, KeyEventArgs k) { if (automatic && k.KeyCode == Keys.F8) { k.Handled = true; StopMouseBooking("F8로 중지했습니다. 안내문은 직접 확인하세요."); } };
                dialog.Deactivate += delegate { if (automatic && !acknowledged && mouseBookingActive) StopMouseBooking("안내창 포커스가 바뀌어 중지했습니다."); };
                if (e.Kind != CoreWebView2ScriptDialogKind.Alert)
                {
                    var cancel = new Button { Text = "취소", Dock = DockStyle.Bottom, Height = 36, DialogResult = DialogResult.Cancel };
                    dialog.Controls.Add(cancel); dialog.CancelButton = cancel;
                }
                if (e.Kind == CoreWebView2ScriptDialogKind.Prompt)
                {
                    prompt = new TextBox { Dock = DockStyle.Bottom, Text = e.DefaultText ?? "" }; dialog.Controls.Add(prompt);
                }
                if (automatic) dialog.Shown += async delegate
                {
                    await Task.Delay(350);
                    if (mouseBookingActive && run == mouseGeneration && NativeMouseInput.IsForeground(dialog) && NativeMouseInput.ForeignInputVersion() == mouseLastInput)
                    {
                        try { NativeMouseInput.PressEnter(dialog); }
                        catch { StopMouseBooking("정차 안내를 자동으로 닫지 못했습니다. 직접 확인하세요."); }
                    }
                    else if (mouseBookingActive) StopMouseBooking("안내창 입력 상태가 바뀌어 자동 진행을 중지했습니다.");
                };
                if (mouseBookingTimer != null) mouseBookingTimer.Stop();
                if (dialog.ShowDialog(this) == DialogResult.OK) { if (prompt != null) e.ResultText = prompt.Text; e.Accept(); }
                else if (automatic) StopMouseBooking("정차 안내 확인을 취소했습니다.");
                if (mouseBookingActive && run == mouseGeneration)
                {
                    if (NativeMouseInput.ForeignInputVersion() != mouseLastInput || NativeMouseInput.IsStopPressed()) StopMouseBooking("사용자 입력으로 자동 진행을 중지했습니다.");
                    else mouseBookingTimer.Start();
                }
            }
        }
        finally { deferral.Complete(); }
    }
    internal static bool AllowedStopNotice(string text)
    {
        string value = Regex.Replace(text ?? "", @"\s+", "");
        return Regex.IsMatch(value, @"^[가-힣]{1,12}(?:역에|역을|역)?정차하는열차입니다\.?도착시간을확인하시기바랍니다\.?$");
    }
    async void ShowBookingHistory()
    {
        StopMouseBooking("예약 기록을 확인합니다.");
        try
        {
            var store = new BookingAttemptStore(Path.Combine(Program.DataRoot, "booking-attempts.json"));
            var items = store.GetAll().ToList();
            if (items.Count == 0) { MessageBox.Show(this, "예약 시도 기록이 없습니다.", "예약 기록"); return; }
            string completionCheckId = null;
            using (var dialog = new Form { Text = "예약 시도 기록", ClientSize = new Size(720, 350), StartPosition = FormStartPosition.CenterParent, Font = Font })
            {
                var list = new ListBox { Dock = DockStyle.Fill }; foreach (var item in items) list.Items.Add(item.Target.Date + " · " + item.Target.Train + " · " + item.Target.Grade + " · " + item.State);
                var hint = new Label { Dock = DockStyle.Top, Height = 100, Padding = new Padding(12), Text = "한 번 제출한 여정은 다시 예매하지 않습니다. 코레일 예약내역에서 결과를 직접 확인하세요.\n결과가 불명확한 기록은 해당 예약 상세를 연 뒤 완료 여부만 다시 확인할 수 있습니다.\n기록 해제는 실제 예약 취소가 아니며, 같은 열차를 다시 예약할 수 있게 합니다." };
                var checkCompletion = new Button { Dock = DockStyle.Bottom, Height = 44, Text = "현재 화면에서 완료 확인", Enabled = false };
                list.SelectedIndexChanged += delegate
                {
                    int i = list.SelectedIndex;
                    checkCompletion.Enabled = i >= 0 && (items[i].State == BookingAttemptState.Submitted || items[i].State == BookingAttemptState.Unknown) && items[i].SubmittedUtc != null;
                };
                checkCompletion.Click += delegate
                {
                    int i = list.SelectedIndex;
                    if (i < 0 || !checkCompletion.Enabled) return;
                    completionCheckId = items[i].Id; dialog.Close();
                };
                var release = new Button { Dock = DockStyle.Bottom, Height = 44, Text = "실제 결과를 확인한 선택 기록의 중복 방지 해제" };
                release.Click += delegate
                {
                    int i = list.SelectedIndex; if (i < 0) return;
                    if (MessageBox.Show(dialog, "코레일 예약내역을 확인했고, 이 여정에 유지할 예약이 없습니까?\n해제 후에는 같은 열차의 예약을 다시 시도할 수 있습니다.", "중복 방지 해제", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    store.ReleaseAfterUserConfirmed(items[i].Id, true); dialog.Close();
                };
                dialog.Controls.Add(list); dialog.Controls.Add(hint); dialog.Controls.Add(checkCompletion); dialog.Controls.Add(release); dialog.ShowDialog(this);
            }
            if (completionCheckId != null) await StartMouseCompletionCheck(completionCheckId);
        }
        catch (Exception ex) { MessageBox.Show(this, "예약 기록을 열지 못했습니다. " + ex.Message, "예약 기록"); }
    }
}
