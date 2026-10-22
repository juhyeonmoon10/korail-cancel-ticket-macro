using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed class ClickNotice
{
    public string Id, Nonce, Train, Departure, Arrival, Grade;
    public bool Manual, Demo;
    public JourneySettings Journey;
    public DateTime PreparedAt;
    public string Message()
    {
        string journey = Journey != null && Journey.Enabled
            ? "\n설정한 여정: " + Journey.Date + " · " + Journey.DepartureStation + " → " + Journey.ArrivalStation
            : "";
        return "코레일 도우미 · 예매 버튼 클릭 감지\n" + Train + " · " + Departure + " → " + Arrival + " · " + Grade + journey
            + "\n클릭 방식: " + (Manual ? "직접 클릭" : "도우미 1회 클릭")
            + "\n시각: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + "\n예약 확정 또는 결제 완료 알림이 아닙니다. 공식 화면에서 예약 결과와 결제 기한을 확인하세요.";
    }
}

// A short-lived page grant survives a click's immediate navigation, but cannot
// authorize any lease or status messages. Dedupe happens before network work.
internal sealed class NotificationInbox
{
    sealed class PageGrant
    {
        public HashSet<string> Sources = new HashSet<string>();
        public bool Demo;
        public JourneySettings Journey;
        public DateTime Expires = DateTime.MaxValue;
    }
    readonly Dictionary<string, PageGrant> pages = new Dictionary<string, PageGrant>();
    readonly Dictionary<string, ClickNotice> pending = new Dictionary<string, ClickNotice>();
    readonly HashSet<string> consumed = new HashSet<string>();
    readonly Queue<string> consumedOrder = new Queue<string>();
    readonly Func<DateTime> now;
    public NotificationInbox() : this(() => DateTime.UtcNow) { }
    internal NotificationInbox(Func<DateTime> clock) { now = clock; }
    public void Register(string nonce, string source, JourneySettings journey, bool demo)
    {
        Prune();
        PageGrant existing;
        if (pages.TryGetValue(nonce, out existing)) { existing.Sources.Add(source); return; }
        if (pages.Count >= 8)
        {
            string oldest = pages.OrderBy(p => p.Value.Expires).First().Key;
            pages.Remove(oldest);
        }
        var grant = new PageGrant { Journey = journey == null ? null : journey.Copy(), Demo = demo };
        grant.Sources.Add(source); pages[nonce] = grant;
    }
    public void Retire(string nonce)
    {
        PageGrant grant;
        if (pages.TryGetValue(nonce, out grant)) grant.Expires = now().AddSeconds(10);
        Prune();
    }
    void Prune()
    {
        foreach (string key in pages.Where(p => p.Value.Expires <= now()).Select(p => p.Key).ToArray()) pages.Remove(key);
        foreach (string key in pending.Where(p => !pages.ContainsKey(p.Value.Nonce) || p.Value.PreparedAt.AddSeconds(30) <= now()).Select(p => p.Key).ToArray()) pending.Remove(key);
    }
    static string Text(Dictionary<string, object> data, string key)
    { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
    static bool Flag(Dictionary<string, object> data, string key)
    { object value; return data.TryGetValue(key, out value) && value is bool && (bool)value; }
    static bool Clock(string value) { return Regex.IsMatch(value, @"^(?:[01]\d|2[0-3]):[0-5]\d$"); }
    ClickNotice ReadNotice(Dictionary<string, object> data, string id, string nonce, PageGrant grant)
    {
        string train = Text(data, "train"), dep = Text(data, "departure"), arr = Text(data, "arrival"), grade = Text(data, "grade");
        if (train.Length < 1 || train.Length > 80 || train.Any(Char.IsControl) || !Clock(dep) || !Clock(arr)
            || !(grade == "일반실" || grade == "특실" || grade == "우등실")) return null;
        return new ClickNotice { Id = id, Nonce = nonce, Train = train, Departure = dep, Arrival = arr, Grade = grade,
            Manual = Flag(data, "manual"), Demo = grant.Demo, Journey = grant.Journey, PreparedAt = now() };
    }
    public bool Handle(string source, Dictionary<string, object> data, out ClickNotice notice)
    {
        notice = null; Prune();
        string nonce = Text(data, "nonce"), id = Text(data, "id"), phase = Text(data, "phase");
        PageGrant grant;
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,80}$") || !pages.TryGetValue(nonce, out grant) || !grant.Sources.Contains(source)) return false;
        string key = nonce + ":" + id;
        if (consumed.Contains(key)) return false;
        if (phase == "cancelled") { pending.Remove(key); Consume(key); return true; }
        if (phase == "prepare")
        {
            var prepared = ReadNotice(data, id, nonce, grant);
            if (prepared == null || pending.Count >= 128) return false;
            if (!pending.ContainsKey(key)) pending[key] = prepared;
            return true;
        }
        if (phase != "clicked") return false;
        ClickNotice candidate;
        if (!pending.TryGetValue(key, out candidate))
        {
            // Keyboard-triggered manual clicks have no pointer-down preparation.
            if (!Flag(data, "manual")) return false;
            candidate = ReadNotice(data, id, nonce, grant);
            if (candidate == null) return false;
        }
        pending.Remove(key); Consume(key); notice = candidate;
        return true;
    }
    void Consume(string key)
    {
        consumed.Add(key); consumedOrder.Enqueue(key);
        while (consumedOrder.Count > 2000) consumed.Remove(consumedOrder.Dequeue());
    }
}
