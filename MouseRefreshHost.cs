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
    int mouseRefreshIntervalSeconds = 60, mouseRefreshLimit = 10, mouseRefreshCount;
    int mouseRefreshJitterPercent;
    readonly Random mouseRefreshRandom = new Random();
    string mouseRefreshQuery, mouseRefreshBaseline, mouseRefreshStableKey;
    double mouseRefreshRevision;
    DateTime mouseRefreshNext, mouseRefreshSent, mouseRefreshStableSince;

    void ResetMouseRefresh()
    {
        mouseAutoRefresh = mouseRefreshOnly = mouseRefreshPending = mouseRefreshSawChange = false;
        mouseRefreshQuery = mouseRefreshStableKey = mouseRefreshBaseline = null;
    }

    async Task OpenMouseRefresh()
    {
        if (!ready || closing) return;
        if (!AutomationAvailable()) return;
        Task stopping = StopHelper("자동 조회 설정 중입니다.");
        int run = mouseGeneration;
        await stopping;
        if (run != mouseGeneration || closing) return;
        using (var dialog = new Form { Text = "마우스 자동 조회", ClientSize = new Size(600, 410), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font })
        {
            var info = new Label { Left = 20, Top = 18, Width = 560, Height = 70,
                Text = "같은 날짜·구간·분류의 조회 버튼을 마우스로 누릅니다.\n최대 10분 · F8 중지 · 조회 대기 중 직접 입력은 일시정지\n창 전환은 일시정지, 차단 안내에서는 중지합니다." };
            var intervalLabel = new Label { Left = 20, Top = 105, Width = 130, Text = "조회 간격 (초)" };
            var interval = new NumericUpDown { Left = 155, Top = 100, Width = 100, Minimum = 30, Maximum = 300, Value = mouseRefreshIntervalSeconds, AccessibleName = "조회 간격" };
            var countLabel = new Label { Left = 300, Top = 105, Width = 150, Text = "추가 조회 횟수" };
            var count = new NumericUpDown { Left = 460, Top = 100, Width = 100, Minimum = 1, Maximum = 20, Value = mouseRefreshLimit };
            var jitterLabel = new Label { Left = 20, Top = 155, Width = 130, Text = "랜덤 지연 (±%)" };
            var jitter = new NumericUpDown { Left = 155, Top = 150, Width = 100, Minimum = 0, Maximum = 100, Value = mouseRefreshJitterPercent, AccessibleName = "랜덤 지연 비율" };
            var range = new Label { Left = 275, Top = 155, Width = 305, Height = 28, AccessibleName = "실제 대기 범위" };
            Action showRange = delegate {
                var policy = new RefreshDelayPolicy((int)interval.Value, (int)jitter.Value);
                range.Text = "실제 대기: " + policy.MinimumSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "~" + policy.MaximumSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "초";
            };
            interval.ValueChanged += delegate { showRange(); }; jitter.ValueChanged += delegate { showRange(); }; showRange();
            var delayNote = new Label { Left = 20, Top = 190, Width = 560, Height = 44, Text = "0%는 고정 간격 · 첫 조회와 갱신 후 매번 대기를 정합니다.\n매크로 감지나 이용 제한을 막는다는 보장은 없습니다." };
            var book = new CheckBox { Left = 20, Top = 240, Width = 560, Height = 32, Text = "미리 선택한 열차의 좌석이 나오면 예매까지 진행 (결제 제외)" };
            var note = new Label { Left = 20, Top = 278, Width = 560, Height = 45, Text = "체크하지 않으면 조회만 합니다.\n예매하려면 열차·등급·확인란과 텔레그램 설정을 완료하세요." };
            var start = new Button { Left = 365, Top = 357, Width = 100, Height = 35, Text = "시작", DialogResult = DialogResult.OK };
            var cancel = new Button { Left = 475, Top = 357, Width = 100, Height = 35, Text = "취소", DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { info, intervalLabel, interval, countLabel, count, jitterLabel, jitter, range, delayNote, book, note, start, cancel });
            dialog.AcceptButton = start; dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            mouseRefreshIntervalSeconds = (int)interval.Value; mouseRefreshLimit = (int)count.Value; mouseRefreshJitterPercent = (int)jitter.Value;
            if (book.Checked) await StartMouseBooking(true);
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
        if (mouseRefreshIntervalSeconds < 30 || mouseRefreshIntervalSeconds > 300 || mouseRefreshJitterPercent < 0 || mouseRefreshJitterPercent > 100 || mouseRefreshLimit < 1 || mouseRefreshLimit > 20)
            throw new InvalidOperationException("조회 간격과 횟수가 허용 범위를 벗어났습니다.");
        var plan = await ReadMouseRefresh(null);
        if (run != mouseGeneration || closing) return false;
        CheckRefreshPlan(plan);
        if (Get(plan, "state") != "ready") throw new InvalidOperationException("첫 조회 결과가 완전히 표시된 뒤 시작하세요.");
        mouseRefreshQuery = Get(plan, "queryFingerprint");
        if (String.IsNullOrEmpty(mouseRefreshQuery)) throw new InvalidOperationException("조회 조건을 고정하지 못했습니다.");
        mouseAutoRefresh = true; mouseRefreshOnly = only; mouseRefreshPending = false; mouseRefreshCount = 0;
        RefreshLog("start", "interval=" + mouseRefreshIntervalSeconds + ", jitterPercent=" + mouseRefreshJitterPercent + ", limit=" + mouseRefreshLimit + ", queryOnly=" + only);
        ScheduleNextMouseRefresh(DateTime.UtcNow);
        return true;
    }

    async Task StartMouseRefreshOnly()
    {
        if (!ready || closing || !AutomationAvailable()) return;
        Task stopping = StopHelper("마우스 자동 조회를 준비합니다.");
        int run = mouseGeneration;
        await stopping;
        if (run != mouseGeneration || closing) return;
        try
        {
            mouseDemo = view.CoreWebView2.Source.StartsWith(LocalBase, StringComparison.Ordinal);
            mousePhase = "select"; mouseSubmitted = false; mouseAttemptId = null; mouseTarget = null;
            mouseReaderNavigation = -1; mouseScrollCount = 0;
            if (!await PrepareMouseRefresh(true, run)) return;
            if (run != mouseGeneration || closing) return;
            if (!NativeMouseInput.IsForeground(this)) throw new InvalidOperationException("프로그램 창을 맨 앞으로 표시한 후 시작하세요.");
            await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.getElementById('korail-one-helper')?.shadowRoot;const m=r?.querySelector('main');if(m){m.hidden=true;r.getElementById('fold').textContent='+';}})()");
            if (run != mouseGeneration || closing) return;
            mouseStarted = mousePhaseStarted = DateTime.UtcNow;
            if (!AutomationAvailable()) return;
            siteDiagnostics.AddAction("start");
            StartMouseLoop();
            status.Text = "조회만 진행 · " + Math.Max(0, Math.Ceiling((mouseRefreshNext - DateTime.UtcNow).TotalSeconds)) + "초 뒤 첫 마우스 조회 · F8로 중지";
        }
        catch (Exception ex) { if (run == mouseGeneration) StopMouseBooking("자동 조회 시작 실패: " + ex.Message); }
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
            if (mouseRefreshCount < mouseRefreshLimit) ScheduleNextMouseRefresh(now);
            RefreshLog("result", "count=" + mouseRefreshCount + ", rows=" + Get(plan, "count"));
            status.Text = "마우스 재조회 " + mouseRefreshCount + "/" + mouseRefreshLimit + "회 결과 갱신 확인";
            if (mouseRefreshOnly && mouseRefreshCount >= mouseRefreshLimit) StopMouseBooking(status.Text + " · 완료");
            return;
        }
        if (mouseRefreshCount >= mouseRefreshLimit) { StopMouseBooking("설정한 추가 조회 " + mouseRefreshLimit + "회를 마쳐 중지했습니다."); return; }
        if (state != "ready") throw new InvalidOperationException("요청하지 않은 로딩 상태가 나타나 자동 조회를 중지했습니다.");
        if (now < mouseRefreshNext)
        {
            status.Text = (mouseRefreshOnly ? "조회만" : "선택 열차 매진") + " · " + Math.Ceiling((mouseRefreshNext - now).TotalSeconds) + "초 뒤 마우스 재조회 · " + mouseRefreshCount + "/" + mouseRefreshLimit + "회 · F8 중지";
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
            if (WindowState == FormWindowState.Minimized || !NativeMouseInput.IsForeground(this)) { InterruptMouseBooking("스크롤 전 창 전환"); return; }
            if (++mouseScrollCount > 8) throw new InvalidOperationException("조회 버튼을 화면에 표시하지 못했습니다.");
            NativeMouseInput.Wheel(this, view.PointToScreen(new Point(view.ClientSize.Width / 2, view.ClientSize.Height / 2)), y < inputMargin ? 360 : -360);
            return;
        }
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
        if (WindowState == FormWindowState.Minimized || !NativeMouseInput.IsForeground(this)) { InterruptMouseBooking("재조회 전 창 전환"); return; }
        mouseRefreshRevision = NumberValue(fresh, "revision"); mouseRefreshBaseline = Get(fresh, "fingerprint");
        mouseRefreshSawChange = false; mouseRefreshStableKey = null; mouseRefreshPending = true;
        mouseRefreshCount++; mouseRefreshSent = DateTime.UtcNow; mouseScrollCount = 0;
        RefreshLog("attempt", "count=" + mouseRefreshCount);
        var screen = view.PointToScreen(new Point((int)Math.Round(x * view.ClientSize.Width / width), (int)Math.Round(y * view.ClientSize.Height / height)));
        if (!AutomationAvailable()) { StopMouseBooking(status.Text); return; }
        siteDiagnostics.AddAction("native-query");
        NativeMouseInput.Click(this, screen);
        RefreshLog("input_sent", "count=" + mouseRefreshCount);
    }

    void RefreshLog(string name, string detail)
    {
        // Local diagnostics contain only counters, never credentials or journey details.
        try { File.AppendAllText(Path.Combine(Program.DataRoot, mouseDemo ? "demo-mouse-refresh.log" : "mouse-refresh.log"), DateTimeOffset.Now.ToString("o") + " " + name + " " + detail + Environment.NewLine); }
        catch { /* Logging cannot cause a retry. */ }
    }
}
