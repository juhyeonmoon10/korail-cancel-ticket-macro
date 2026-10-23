using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    bool mouseReadAll, mouseMorePending;
    string mouseMoreQuery, mouseMoreStableKey;
    string[] mouseMoreBaseline;
    int mouseMoreCount, mouseMoreScrollCount;
    DateTime mouseMoreSent, mouseMoreStableSince;
    const int MorePageLimit = 30;

    void FinishTrainReadRequest()
    {
        try { if (ready && !closing) view.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { kind = "trainReadFinished", nonce = nonce })); }
        catch { }
    }

    void ResetMouseMore()
    {
        bool reading = mouseReadAll;
        mouseReadAll = mouseMorePending = false;
        mouseMoreQuery = mouseMoreStableKey = null; mouseMoreBaseline = null;
        mouseMoreCount = mouseMoreScrollCount = 0;
        if (reading) FinishTrainReadRequest();
    }

    void CheckMouseMoreDeadline()
    {
        if (mouseMorePending && (DateTime.UtcNow - mouseMoreSent).TotalSeconds > 30)
            throw new InvalidOperationException("더보기 결과가 30초 안에 확인되지 않아 추가 클릭 없이 중지했습니다.");
    }

    static string[] MoreKeys(Dictionary<string, object> plan)
    {
        object raw;
        if (plan == null || !plan.TryGetValue("keys", out raw) || raw is string || !(raw is IEnumerable))
            throw new InvalidOperationException("열차 목록 식별 정보를 확인하지 못했습니다.");
        var result = new List<string>();
        foreach (object value in (IEnumerable)raw)
        {
            string key = value as string;
            if (String.IsNullOrEmpty(key) || key.Length > 250 || result.Contains(key) || result.Count >= 2000)
                throw new InvalidOperationException("열차 목록이 중복되거나 허용 크기를 벗어났습니다.");
            result.Add(key);
        }
        return result.ToArray();
    }

    static bool MoreAppended(string[] previous, string[] current)
    {
        if (previous == null || current.Length < previous.Length || !previous.SequenceEqual(current.Take(previous.Length)))
            throw new InvalidOperationException("더보기 중 기존 열차 목록이 바뀌었습니다. 다시 조회하고 열차를 선택하세요.");
        return current.Length > previous.Length;
    }

    async Task<Dictionary<string, object>> ReadMouseMore()
    {
        int run = mouseGeneration;
        await EnsureMouseReader();
        if (run != mouseGeneration || closing) return null;
        if (!await EvalBool("!!window.KorailMouseMore"))
            await view.CoreWebView2.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BundleRoot, "mouse-refresh.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "mouse-more.js")));
        if (run != mouseGeneration || closing) return null;
        object[] targets = mouseReadAll || mouseTargets == null ? new object[0] : mouseTargets.Select(MouseTargetData).ToArray();
        string raw = await view.CoreWebView2.ExecuteScriptAsync("window.KorailMouseMore.read(" + json.Serialize(mouseMoreQuery ?? mouseRefreshQuery) + "," + json.Serialize(targets) + ")");
        if (run != mouseGeneration || closing) return null;
        return json.Deserialize<Dictionary<string, object>>(raw);
    }

    void CheckMorePlan(Dictionary<string, object> plan)
    {
        CheckRefreshPlan(plan);
        if (Get(plan, "state") != "ready" && Get(plan, "state") != "loading")
            throw new InvalidOperationException("더보기 화면을 확인하지 못했습니다.");
        string query = Get(plan, "queryFingerprint");
        if (String.IsNullOrEmpty(query) || (mouseMoreQuery != null && query != mouseMoreQuery))
            throw new InvalidOperationException("더보기 중 조회 조건이 바뀌었습니다.");
    }

    static bool MoreFlag(Dictionary<string, object> plan, string name)
    { object value; return plan != null && plan.TryGetValue(name, out value) && Object.Equals(value, true); }

    async Task StartMouseReadAll()
    {
        if (!ready || closing || !AutomationAvailable() || mouseBookingActive) { FinishTrainReadRequest(); return; }
        Task stopping = StopHelper("더보기를 펼쳐 전체 열차 목록을 읽습니다.");
        int run = mouseGeneration;
        await stopping;
        if (run != mouseGeneration || closing) { FinishTrainReadRequest(); return; }
        try
        {
            mouseDemo = view.CoreWebView2.Source.StartsWith(LocalBase, StringComparison.Ordinal);
            mouseReadAll = true; mouseSubmitted = mouseCompletionRecheck = false; mouseAttemptId = null;
            mouseTarget = null; mouseTargets = null; mousePhase = "select";
            mouseReaderNavigation = -1; mouseStarted = mousePhaseStarted = DateTime.UtcNow;
            await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.getElementById('korail-one-helper')?.shadowRoot;const m=r?.querySelector('main');if(m){m.hidden=true;r.getElementById('fold').textContent='+';}})()");
            if (run != mouseGeneration || closing) return;
            var plan = await ReadMouseMore();
            if (run != mouseGeneration || closing) return;
            CheckMorePlan(plan);
            if (Get(plan, "state") != "ready") throw new InvalidOperationException("조회 결과가 표시된 뒤 목록을 읽어주세요.");
            mouseMoreQuery = Get(plan, "queryFingerprint");
            if (!InputWindowReady()) throw new InvalidOperationException("도우미 창을 열고 목록을 읽어주세요. 마우스 모드에서는 맨 앞으로 표시하세요.");
            if (!AutomationAvailable()) { StopMouseBooking(status.Text); return; }
            StartMouseLoop();
            status.Text = "더보기 포함 열차 목록 읽기 · 클릭·창 전환 시 일시정지 · F8 중지";
        }
        catch (Exception ex) { if (run == mouseGeneration) { StopMouseBooking("목록 읽기 실패: " + ex.Message); FinishTrainReadRequest(); } }
    }

    // Returns true while expansion owns the tick; false permits the ordinary seat reader.
    async Task<bool> TickMouseMore(int run)
    {
        if (mousePhase != "select" || mouseSubmitted || mouseCompletionRecheck)
            throw new InvalidOperationException("예약 진행 중에는 더보기를 누를 수 없습니다.");
        CheckMouseMoreDeadline();
        var plan = await ReadMouseMore();
        if (run != mouseGeneration || !mouseBookingActive) return true;
        CheckMouseMoreDeadline();
        CheckMorePlan(plan);
        if (run != mouseGeneration || !mouseBookingActive) return true;
        if (mouseMoreQuery == null) mouseMoreQuery = Get(plan, "queryFingerprint");
        string state = Get(plan, "state");
        if (mouseMorePending)
        {
            if (state == "loading") { mouseMoreStableKey = null; return true; }
            string[] keys = MoreKeys(plan);
            if (!MoreAppended(mouseMoreBaseline, keys)) { mouseMoreStableKey = null; return true; }
            string stable = json.Serialize(keys);
            if (stable != mouseMoreStableKey) { mouseMoreStableKey = stable; mouseMoreStableSince = DateTime.UtcNow; return true; }
            if ((DateTime.UtcNow - mouseMoreStableSince).TotalMilliseconds < 1000) return true;
            mouseMorePending = false; mouseMoreScrollCount = 0;
            status.Text = "더보기 " + mouseMoreCount + "회 · " + keys.Length + "개 열차 확인";
            return true;
        }
        if (state != "ready") throw new InvalidOperationException("예상하지 않은 목록 갱신이 발생했습니다. 조회가 끝난 뒤 다시 시작하세요.");
        if (!mouseReadAll && MoreFlag(plan, "allTargetsPresent")) return false;
        if (!MoreFlag(plan, "more"))
        {
            if (!mouseReadAll) throw new InvalidOperationException("더보기 끝까지 확인했지만 선택한 열차 일부가 없습니다. 조건을 확인하세요.");
            int count = MoreKeys(plan).Length;
            bool scanned = await EvalBool("window.KorailDesktopHelper.scanTrains()");
            if (run != mouseGeneration || !mouseBookingActive) return true;
            StopMouseBooking(scanned ? "더보기 포함 " + count + "개 열차 읽기 완료 · 원하는 열차를 선택하세요." : "열차 목록을 적용하지 못했습니다. 도우미 안내를 확인하세요.");
            return true;
        }
        if (mouseMoreCount >= MorePageLimit) throw new InvalidOperationException("더보기 30회 한도에 도달했습니다. 출발 시각을 좁혀 다시 조회하세요.");
        var action = ObjectValue(plan, "action");
        if (action == null || Get(action, "kind") != "more") throw new InvalidOperationException("더보기 버튼을 하나로 확인하지 못했습니다.");
        double x = NumberValue(action, "x"), y = NumberValue(action, "y"), width = NumberValue(action, "viewportWidth"), height = NumberValue(action, "viewportHeight");
        if (width <= 0 || height <= 0 || x < 0 || x >= width) throw new InvalidOperationException("더보기 버튼의 위치를 확인하지 못했습니다.");
        if (!CheckMoreInput()) return true;
        if (y < 64 || y >= height - 64)
        {
            if (++mouseMoreScrollCount > 100) throw new InvalidOperationException("더보기 버튼을 화면에 표시하지 못했습니다.");
            await WheelAutomation(y < 64 ? 480 : -480, run);
            return true;
        }
        Point origin = view.PointToScreen(Point.Empty); Size viewport = view.ClientSize;
        var fresh = await ReadMouseMore();
        if (run != mouseGeneration || !mouseBookingActive) return true;
        CheckMorePlan(fresh);
        var point = ObjectValue(fresh, "action");
        if (Get(fresh, "state") != "ready" || point == null || Get(point, "kind") != "more" || Get(point, "id") != Get(action, "id") ||
            !MoreKeys(plan).SequenceEqual(MoreKeys(fresh)) || Math.Abs(NumberValue(point, "x") - x) > 1 || Math.Abs(NumberValue(point, "y") - y) > 1 ||
            NumberValue(point, "viewportWidth") != width || NumberValue(point, "viewportHeight") != height || origin != view.PointToScreen(Point.Empty) || viewport != view.ClientSize) return true;
        if (!CheckMoreInput()) return true;
        mouseMoreBaseline = MoreKeys(fresh); mouseMorePending = true; mouseMoreSent = DateTime.UtcNow;
        mouseMoreStableKey = null; mouseMoreCount++; mouseMoreScrollCount = 0;
        siteDiagnostics.AddAction(BackgroundInputEnabled ? "page-more" : "native-more");
        await ClickAutomation(x, y, width, height, run);
        if (run != mouseGeneration || !mouseBookingActive) return true;
        status.Text = "더보기 " + mouseMoreCount + "회 클릭 · 새 열차가 추가되는지 확인 중…";
        return true;
    }

    bool CheckMoreInput()
    {
        if (NativeMouseInput.IsStopPressed()) throw new InvalidOperationException("F8로 중지했습니다.");
        if (NativeMouseInput.ForeignInputVersion() != mouseLastInput) { InterruptMouseBooking("더보기 전 직접 입력 감지"); return false; }
        if (!InputWindowReady()) { InterruptMouseBooking("더보기 대상 창 비활성"); return false; }
        return true;
    }
}
