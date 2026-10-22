using System;
using System.Collections.Generic;

// Runs only against NotificationInbox and message formatting. No browser or network.
internal static class NotificationTests
{
    const string Source = "https://www.korail.com/ticket/main";
    static int count;
    static int Main()
    {
        try { Run(); Console.WriteLine("PASS " + count + " notification handoff checks (no network)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
    }
    static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static JourneySettings Journey()
    {
        return new JourneySettings { Enabled = true, Date = "2026-10-25", DepartureStation = "서울", ArrivalStation = "부산", Grade = "일반실" };
    }
    static Dictionary<string, object> Event(string phase, string id, string nonce, bool manual)
    {
        return new Dictionary<string, object> {
            { "phase", phase }, { "id", id }, { "nonce", nonce }, { "manual", manual },
            { "train", "KTX 101" }, { "departure", "07:30" }, { "arrival", "10:15" }, { "grade", "일반실" }
        };
    }
    static void Run()
    {
        DateTime now = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);
        var box = new NotificationInbox(() => now);
        box.Register("page-a", Source, Journey(), false);
        ClickNotice notice;
        Check(box.Handle(Source, Event("prepare", "one", "page-a", false), out notice) && notice == null, "prepare acknowledges without notifying");
        Check(box.Handle(Source, Event("clicked", "one", "page-a", false), out notice) && notice != null && notice.Train == "KTX 101" && !notice.Manual, "prepared automatic click produces one notice");
        Check(!box.Handle(Source, Event("clicked", "one", "page-a", false), out notice) && notice == null, "duplicate clicked event is suppressed");
        Check(!box.Handle(Source, Event("prepare", "one", "page-a", false), out notice), "consumed ID cannot be prepared again");
        Check(!box.Handle(Source, Event("clicked", "unprepared-auto", "page-a", false), out notice), "automatic click without preparation is rejected");
        Check(box.Handle(Source, Event("clicked", "keyboard", "page-a", true), out notice) && notice != null && notice.Manual, "manual keyboard click works without preparation");
        Check(!box.Handle("https://example.org/ticket/main", Event("clicked", "wrong-origin", "page-a", true), out notice), "wrong origin is rejected");
        Check(!box.Handle(Source + "?other", Event("clicked", "wrong-source", "page-a", true), out notice), "unregistered source on same origin is rejected");
        Check(!box.Handle(Source, Event("clicked", "wrong-nonce", "unknown", true), out notice), "wrong nonce is rejected");
        Check(!box.Handle(Source, Event("unknown", "unknown-phase", "page-a", true), out notice), "unknown phase is rejected");
        Check(!box.Handle(Source, Event("clicked", "invalid id", "page-a", true), out notice), "invalid event ID is rejected");

        Check(box.Handle(Source, Event("prepare", "cancelled", "page-a", false), out notice)
            && box.Handle(Source, Event("cancelled", "cancelled", "page-a", false), out notice) && notice == null,
            "cancellation removes preparation without notifying");
        Check(!box.Handle(Source, Event("clicked", "cancelled", "page-a", false), out notice), "cancelled event cannot notify later");

        var prepared = Event("prepare", "immutable", "page-a", false);
        box.Handle(Source, prepared, out notice);
        var changed = Event("clicked", "immutable", "page-a", false); changed["train"] = "KTX 999"; changed["grade"] = "특실";
        Check(box.Handle(Source, changed, out notice) && notice.Train == "KTX 101" && notice.Grade == "일반실", "click metadata cannot replace prepared train and grade");

        box.Handle(Source, Event("prepare", "race", "page-a", false), out notice);
        box.Retire("page-a");
        string next = "https://www.korail.com/ticket/reservation";
        box.Register("page-b", next, Journey(), false);
        Check(box.Handle(Source, Event("clicked", "race", "page-a", false), out notice) && notice != null, "old page click survives navigation nonce rotation");
        Check(!box.Handle(next, Event("clicked", "wrong-pair", "page-a", true), out notice), "new source cannot use retired nonce");
        now = now.AddSeconds(31);
        Check(!box.Handle(Source, Event("clicked", "expired-page", "page-a", true), out notice), "retired page grant expires");

        box.Handle(next, Event("prepare", "expired-prepare", "page-b", false), out notice);
        now = now.AddSeconds(31);
        Check(!box.Handle(next, Event("clicked", "expired-prepare", "page-b", false), out notice), "abandoned automatic preparation expires on active page");

        string spa = next + "?step=2";
        box.Register("page-b", spa, Journey(), false);
        Check(box.Handle(next, Event("clicked", "spa-old", "page-b", true), out notice) && notice != null,
            "registered SPA source before update remains accepted");
        Check(box.Handle(spa, Event("clicked", "spa-new", "page-b", true), out notice) && notice != null,
            "registered SPA source after update is accepted");

        var originalJourney = Journey();
        box.Register("copy", Source, originalJourney, false);
        originalJourney.DepartureStation = "변경됨";
        box.Handle(Source, Event("prepare", "journey-copy", "copy", false), out notice);
        Check(box.Handle(Source, Event("clicked", "journey-copy", "copy", false), out notice) && notice.Journey.DepartureStation == "서울",
            "page grant copies journey settings instead of sharing mutable UI settings");
        string message = notice.Message();
        Check(message.Contains("예매 버튼 클릭 감지") && message.Contains("예약 확정 또는 결제 완료 알림이 아닙니다.")
            && message.Contains("KTX 101") && message.Contains("07:30") && message.Contains("서울 → 부산") && message.Contains("도우미 1회 클릭"),
            "message describes attempted click with train and configured journey, never confirms booking");

        box.Register("demo", "https://korail-helper.invalid/demo.html", Journey(), true);
        var demo = Event("clicked", "demo-native", "demo", true); demo["demo"] = false;
        Check(box.Handle("https://korail-helper.invalid/demo.html", demo, out notice) && notice.Demo, "demo flag derives from native page grant despite false page field");
        var live = Event("clicked", "live-native", "copy", true); live["demo"] = true;
        Check(box.Handle(Source, live, out notice) && !notice.Demo && notice.Message().Contains("직접 클릭"), "page cannot mark live click as demo; manual wording is accurate");

        string[] fields = { "train", "departure", "arrival", "grade" };
        string[] values = { "KTX 101\nInjected", "24:00", "10:60", "예약대기" };
        for (int i = 0; i < fields.Length; i++)
        {
            var invalid = Event("clicked", "bad" + i, "copy", true); invalid[fields[i]] = values[i];
            Check(!box.Handle(Source, invalid, out notice), "invalid " + fields[i] + " payload is rejected");
        }
        var stringManual = Event("clicked", "string-manual", "copy", false); stringManual["manual"] = "true";
        Check(!box.Handle(Source, stringManual, out notice), "string manual flag cannot bypass preparation");

        var bounded = new NotificationInbox(() => now);
        for (int i = 0; i < 9; i++) { bounded.Register("bounded" + i, Source, Journey(), false); now = now.AddMilliseconds(1); }
        Check(!bounded.Handle(Source, Event("clicked", "oldest", "bounded0", true), out notice), "page grant count is bounded and oldest grant retires");
        Check(bounded.Handle(Source, Event("clicked", "latest", "bounded8", true), out notice) && notice != null, "latest grant remains usable after capacity limit");
    }
}
