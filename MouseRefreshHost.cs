using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    bool mouseAutoRefresh, mouseRefreshOnly, mouseRefreshPending, mouseRefreshSawChange;
    bool mouseRefreshSiteBlocked;
    int mouseRefreshIntervalSeconds = 60, mouseRefreshLimit = 10;
    long mouseRefreshCount;
    bool mouseRefreshUnlimited, mouseRefreshProgressStarted;
    BookingCompletionMode mouseBookingCompletionMode = BookingCompletionMode.Any;
    readonly Label refreshProgress = new Label { AccessibleName = "누적 조회 및 남은 시간" };
    System.Windows.Forms.Timer refreshDisplayTimer;
    int mouseRefreshJitterPercent;
    readonly Random mouseRefreshRandom = new Random();
    string mouseRefreshQuery, mouseRefreshBaseline, mouseRefreshStableKey;
    double mouseRefreshRevision;
    DateTime mouseRefreshNext, mouseRefreshSent, mouseRefreshStableSince;

    RefreshOptions CurrentRefreshOptions()
    { return new RefreshOptions(mouseRefreshIntervalSeconds, mouseRefreshLimit, mouseRefreshJitterPercent, mouseRefreshUnlimited, mouseBookingCompletionMode); }

    bool MouseRefreshLimitReached { get { return !mouseRefreshUnlimited && mouseRefreshCount >= mouseRefreshLimit; } }

    string CurrentMouseRefreshCountText()
    { return mouseRefreshCount.ToString(CultureInfo.InvariantCulture) + (mouseRefreshUnlimited ? "회 · 무한 반복" : "/" + mouseRefreshLimit.ToString(CultureInfo.InvariantCulture) + "회"); }

    static string RefreshSecondsRemaining(DateTime deadline, DateTime now)
    { return Math.Max(0, (deadline - now).TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture) + "초"; }

    void UpdateRefreshProgress()
    {
        if (closing || refreshProgress.IsDisposed) return;
        if (!mouseRefreshProgressStarted) { refreshProgress.Text = "추가 조회를 시작하면 누적 횟수와 남은 시간이 표시됩니다."; return; }
        string phase = mousePaused ? "일시정지" : !mouseBookingActive ? (queueRunning ? (queueStage == "notifying" ? "예약 알림 처리 중" : "다음 조회 준비 중") : "대기 / 종료")
            : mouseRefreshPending ? "결과 확인 중"
            : !mouseAutoRefresh || mousePhase != "select" ? "예약 처리 중"
            : mouseMorePending || mouseReadAll ? "더보기 확인 중"
            : "재조회까지 " + RefreshSecondsRemaining(mouseRefreshNext, DateTime.UtcNow);
        refreshProgress.Text = "누적 추가 조회 시도 " + CurrentMouseRefreshCountText() + " · " + phase;
    }

    void StartRefreshDisplay()
    {
        mouseRefreshProgressStarted = true;
        if (refreshDisplayTimer == null)
        {
            // Presentation only: never increase the WebView/read/input frequency.
            refreshDisplayTimer = new System.Windows.Forms.Timer { Interval = 50 };
            refreshDisplayTimer.Tick += delegate { UpdateRefreshProgress(); };
            FormClosed += delegate { refreshDisplayTimer.Stop(); refreshDisplayTimer.Dispose(); };
        }
        refreshDisplayTimer.Start();
        UpdateRefreshProgress();
    }

    void ResetMouseRefresh()
    {
        mouseAutoRefresh = mouseRefreshOnly = mouseRefreshPending = mouseRefreshSawChange = false;
        mouseRefreshQuery = mouseRefreshStableKey = mouseRefreshBaseline = null;
        if (refreshDisplayTimer != null) refreshDisplayTimer.Stop();
        UpdateRefreshProgress();
    }

    async Task OpenMouseRefresh()
    {
        if (!ready || closing) return;
        if (!AutomationAvailable()) return;
        Task stopping = StopHelper("자동 조회 설정 중입니다.");
        int run = mouseGeneration;
        await stopping;
        if (run != mouseGeneration || closing) return;
        using (var dialog = new RefreshSettingsDialog(CurrentRefreshOptions(), Font))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            mouseRefreshIntervalSeconds = dialog.SelectedOptions.IntervalSeconds;
            mouseRefreshLimit = dialog.SelectedOptions.Limit; mouseRefreshJitterPercent = dialog.SelectedOptions.JitterPercent;
            mouseRefreshUnlimited = dialog.SelectedOptions.Unlimited; mouseBookingCompletionMode = dialog.SelectedOptions.CompletionMode;
            if (dialog.BookWhenAvailable) await StartMouseBooking(true);
            else await StartMouseRefreshOnly();
        }
    }

    void ScheduleNextMouseRefresh(DateTime now)
    {
        // Draw only when a new wait begins, never on a timer tick or a retry.
        var delay = new RefreshDelayPolicy(mouseRefreshIntervalSeconds, mouseRefreshJitterPercent).Sample(mouseRefreshRandom.NextDouble());
        mouseRefreshNext = now.Add(delay);
        RefreshLog("wait", "seconds=" + delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
    }

    async Task<Dictionary<string, object>> ReadMouseRefresh(string expectedQuery)
    {
        await EnsureMouseReader();
        if (!await EvalBool("!!window.KorailMouseRefresh"))
            await view.CoreWebView2.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BundleRoot, "mouse-refresh.js")));
        string expected = expectedQuery == null ? "null" : json.Serialize(new { queryFingerprint = expectedQuery });
        string raw = await view.CoreWebView2.ExecuteScriptAsync("window.KorailMouseRefresh.read(" + expected + ")");
        return json.Deserialize<Dictionary<string, object>>(raw);
    }

    void CheckRefreshPlan(Dictionary<string, object> plan)
    {
        if (plan == null) throw new InvalidOperationException("재조회 화면을 읽지 못했습니다.");
        if (!String.IsNullOrEmpty(Get(plan, "blockCode"))) ObserveSiteBlock(Get(plan, "blockCode"));
        if (Get(plan, "state") == "blocked" || Get(plan, "state") == "unknown")
            throw new InvalidOperationException(Get(plan, "message"));
    }

    async Task<bool> PrepareMouseRefresh(bool only, int run)
    {
        if (mouseRefreshSiteBlocked) throw new InvalidOperationException("이 실행에서 코레일 자동화 차단이 확인되었습니다. 자동 조회를 다시 시작하지 않습니다.");
        CurrentRefreshOptions();
        var plan = await ReadMouseRefresh(null);
        if (run != mouseGeneration || closing) return false;
        CheckRefreshPlan(plan);
        if (Get(plan, "state") != "ready") throw new InvalidOperationException("첫 조회 결과가 완전히 표시된 뒤 시작하세요.");
        mouseRefreshQuery = Get(plan, "queryFingerprint");
        if (String.IsNullOrEmpty(mouseRefreshQuery)) throw new InvalidOperationException("조회 조건을 고정하지 못했습니다.");
        mouseAutoRefresh = true; mouseRefreshOnly = only; mouseRefreshPending = false;
        mouseRefreshCount = queueRunning && queueJob != null ? queueJob.RefreshCount : 0;
        RefreshLog("start", "interval=" + mouseRefreshIntervalSeconds + ", jitterPercent=" + mouseRefreshJitterPercent + ", limit=" + (mouseRefreshUnlimited ? "unlimited" : mouseRefreshLimit.ToString(CultureInfo.InvariantCulture)) + ", queryOnly=" + only);
        ScheduleNextMouseRefresh(DateTime.UtcNow);
        StartRefreshDisplay();
        return true;
    }

    async Task StartMouseRefreshOnly()
    {
        if (!ready || closing || !AutomationAvailable()) return;
        Task stopping = StopHelper("마우스 자동 조회를 준비합니다.");
        int run = mouseGeneration;
        await stopping;
        if (run != mouseGeneration || closing) return;
        PrepareAutomationReports("자동 조회");
        try
        {
            mouseDemo = view.CoreWebView2.Source.StartsWith(LocalBase, StringComparison.Ordinal);
            mousePhase = "select"; mouseSubmitted = false; mouseAttemptId = null; mouseTarget = null;
            mouseReaderNavigation = -1; mouseScrollCount = 0;
            if (!await PrepareMouseRefresh(true, run)) return;
            if (run != mouseGeneration || closing) return;
            if (!InputWindowReady()) throw new InvalidOperationException("도우미 창을 열고 시작하세요. 마우스 모드에서는 맨 앞으로 표시하세요.");
            await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.getElementById('korail-one-helper')?.shadowRoot;const m=r?.querySelector('main');if(m){m.hidden=true;r.getElementById('fold').textContent='+';}})()");
            if (run != mouseGeneration || closing) return;
            mouseStarted = mousePhaseStarted = DateTime.UtcNow;
            if (!AutomationAvailable()) return;
            siteDiagnostics.AddAction("start");
            StartMouseLoop();
            status.Text = "조회만 진행 · " + RefreshSecondsRemaining(mouseRefreshNext, DateTime.UtcNow) + " 뒤 첫 조회 · F8로 중지";
            AutomationReportsStarted();
        }
        catch (Exception ex) { if (run == mouseGeneration) { ReportAutomationFailure("자동 조회 시작 실패: " + ex.Message, ex); StopMouseBooking("자동 조회 시작 실패: " + ex.Message); } }
    }

    async Task TickMouseRefresh(int run)
    {
        if (!mouseAutoRefresh || mouseSubmitted || mousePhase != "select") throw new InvalidOperationException("재조회 단계가 아니어서 중지했습니다.");
        var plan = await ReadMouseRefresh(mouseRefreshQuery);
        if (run != mouseGeneration || !mouseBookingActive) return;
        CheckRefreshPlan(plan);
        DateTime now = DateTime.UtcNow;
        string state = Get(plan, "state"), fingerprint = Get(plan, "fingerprint");
        double revision = NumberValue(plan, "revision");
        if (mouseRefreshPending)
        {
            if ((now - mouseRefreshSent).TotalSeconds > 30) throw new InvalidOperationException("30초 안에 재조회 결과 변경을 확인하지 못했습니다. 추가 클릭 없이 중지했습니다.");
            if (state == "loading" || revision > mouseRefreshRevision || fingerprint != mouseRefreshBaseline) mouseRefreshSawChange = true;
            if (state != "ready" || !mouseRefreshSawChange)
            {
                mouseRefreshStableKey = null;
                status.Text = "마우스 조회 " + mouseRefreshCount + "회 클릭 · 결과 갱신 확인 중…";
                return;
            }
            string key = revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + fingerprint;
            if (mouseRefreshStableKey != key) { mouseRefreshStableKey = key; mouseRefreshStableSince = now; return; }
            if ((now - mouseRefreshStableSince).TotalMilliseconds < 750) return;
            mouseRefreshPending = false; mouseScrollCount = 0;
            mouseMoreCount = mouseMoreScrollCount = 0; mouseMoreStableKey = null; mouseMoreBaseline = null;
            if (!MouseRefreshLimitReached) ScheduleNextMouseRefresh(now);
            RefreshLog("result", "count=" + mouseRefreshCount + ", rows=" + Get(plan, "count"));
            status.Text = "재조회 " + CurrentMouseRefreshCountText() + " 결과 갱신 확인";
            ReportAutomationProgress();
            if (mouseRefreshOnly && MouseRefreshLimitReached) StopMouseBooking(status.Text + " · 완료", true);
            return;
        }
        if (MouseRefreshLimitReached) { StopMouseBooking("설정한 추가 조회 " + mouseRefreshLimit + "회를 마쳐 중지했습니다.", true); return; }
        if (state != "ready") throw new InvalidOperationException("요청하지 않은 로딩 상태가 나타나 자동 조회를 중지했습니다.");
        if (now < mouseRefreshNext)
        {
            status.Text = (mouseRefreshOnly ? "조회만" : "선택 열차 매진") + " · " + RefreshSecondsRemaining(mouseRefreshNext, now) + " 뒤 재조회 · " + CurrentMouseRefreshCountText() + " · F8 중지";
            return;
        }
        var action = ObjectValue(plan, "action");
        if (action == null || Get(action, "kind") != "refresh") throw new InvalidOperationException("재조회 버튼을 확인하지 못했습니다.");
        double x = NumberValue(action, "x"), y = NumberValue(action, "y"), width = NumberValue(action, "viewportWidth"), height = NumberValue(action, "viewportHeight");
        if (width <= 0 || height <= 0 || x < 0 || x >= width) throw new InvalidOperationException("조회 버튼이 가로 화면 밖에 있습니다.");
        // Keep controls clear of WebView's native link-status/tooltip overlays.
        // They are outside the DOM and WindowFromPoint correctly rejects them.
        const double inputMargin = 64;
        if (y < inputMargin || y >= height - inputMargin)
        {
            if (NativeMouseInput.ForeignInputVersion() != mouseLastInput) { InterruptMouseBooking("스크롤 전 직접 입력 감지"); return; }
            if (!InputWindowReady()) { InterruptMouseBooking("스크롤 대상 창 비활성"); return; }
            if (++mouseScrollCount > 100) throw new InvalidOperationException("조회 버튼을 화면에 표시하지 못했습니다.");
            await WheelAutomation(y < inputMargin ? 360 : -360, run);
            return;
        }
        await PreparePointerAutomation(x, y, width, height, run);
        if (run != mouseGeneration || !mouseBookingActive) return;
        Point origin = view.PointToScreen(Point.Empty); Size viewport = view.ClientSize;
        if (!mouseRefreshOnly)
        {
            var seat = await ReadMousePlan();
            if (run != mouseGeneration || !mouseBookingActive) return;
            if (Get(seat, "state") == "ready") return; // A newly available seat takes precedence.
            if (!Object.Equals(seat.ContainsKey("refreshEligible") ? seat["refreshEligible"] : null, true))
                throw new InvalidOperationException("재조회 직전 선택 열차의 매진 상태가 바뀌었습니다. " + Get(seat, "message"));
        }
        var fresh = await ReadMouseRefresh(mouseRefreshQuery);
        if (run != mouseGeneration || !mouseBookingActive) return;
        CheckRefreshPlan(fresh);
        if (Get(fresh, "fingerprint") != fingerprint || NumberValue(fresh, "revision") != revision) return;
        var point = ObjectValue(fresh, "action");
        if (Get(fresh, "state") != "ready" || point == null || Get(point, "id") != Get(action, "id") ||
            Math.Abs(NumberValue(point, "x") - x) > 1 || Math.Abs(NumberValue(point, "y") - y) > 1 ||
            NumberValue(point, "viewportWidth") != width || NumberValue(point, "viewportHeight") != height ||
            origin != view.PointToScreen(Point.Empty) || viewport != view.ClientSize) return;
        if (NativeMouseInput.IsStopPressed()) throw new InvalidOperationException("F8로 중지했습니다.");
        if (NativeMouseInput.ForeignInputVersion() != mouseLastInput) { InterruptMouseBooking("재조회 전 직접 입력 감지"); return; }
        if (!InputWindowReady()) { InterruptMouseBooking("재조회 대상 창 비활성"); return; }
        if (!AutomationAvailable()) { StopMouseBooking(status.Text); return; }
        mouseRefreshRevision = NumberValue(fresh, "revision"); mouseRefreshBaseline = Get(fresh, "fingerprint");
        mouseRefreshSawChange = false; mouseRefreshStableKey = null; mouseRefreshPending = true;
        mouseRefreshCount = checked(mouseRefreshCount + 1);
        RecordAutomationQueryAttempt();
        RecordQueuedRefreshCount(mouseRefreshCount);
        mouseRefreshSent = DateTime.UtcNow; mouseScrollCount = 0;
        UpdateRefreshProgress();
        RefreshLog("attempt", "count=" + mouseRefreshCount);
        if (!AutomationAvailable()) { StopMouseBooking(status.Text); return; }
        siteDiagnostics.AddAction(BackgroundInputEnabled ? "page-query" : "native-query");
        await ClickAutomation(x, y, width, height, run);
        if (run != mouseGeneration || !mouseBookingActive) return;
        RefreshLog("input_sent", "count=" + mouseRefreshCount);
    }

    void RefreshLog(string name, string detail)
    {
        // Local diagnostics contain only counters, never credentials or journey details.
        try { File.AppendAllText(Path.Combine(Program.DataRoot, mouseDemo ? "demo-mouse-refresh.log" : "mouse-refresh.log"), DateTimeOffset.Now.ToString("o") + " " + name + " " + detail + Environment.NewLine); }
        catch { /* Logging cannot cause a retry. */ }
    }
}
