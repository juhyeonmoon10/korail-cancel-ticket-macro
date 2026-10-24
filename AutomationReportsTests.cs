using System;
using System.Globalization;

// Deterministic formatting and policy tests. No browser, network or real credentials.
internal static class AutomationReportsTests
{
    static int count;
    const string FakeToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ_fake_token";
    const string FakeChatId = "-1009876543210";
    static int Main()
    {
        try { Run(); Console.WriteLine("PASS " + count + " automation report checks (no network)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); return 1; }
    }
    static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static TelegramSettings Settings()
    {
        return new TelegramSettings { Enabled = true, BotToken = FakeToken, ChatId = FakeChatId,
            StartNotificationsEnabled = true, ErrorNotificationsEnabled = true,
            ProgressNotificationsEnabled = true, ProgressEveryQueries = 50 };
    }
    static AutomationReportSnapshot Snapshot(long queries)
    {
        return new AutomationReportSnapshot { QueryCount = queries, JobIndex = 1, JobCount = 2,
            Stage = "좌석 확인", Status = "조건에 맞는 좌석을 기다리는 중입니다.",
            TargetSummary = "2026-10-28 서울 → 부산 · KTX 101, 103 · 일반실" };
    }
    static AutomationReportError Failure()
    {
        return new AutomationReportError { Code = "BROWSER_EXIT", Category = "브라우저 종료",
            ObservedReason = "예약 화면을 표시하던 브라우저 프로세스가 종료됐습니다.",
            RequiredAction = "공식 예약 내역을 확인한 뒤 도우미를 다시 실행하고 로그인하세요." };
    }
    static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (Char.IsSurrogate(text[i]))
            {
                if (!Char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !Char.IsLowSurrogate(text[++i])) return false;
            }
        return true;
    }
    static void Run()
    {
        var session = new AutomationReportSession(Settings());
        Check(session.TryProgress(Snapshot(50)) == null, "progress before start is ignored");
        string start = session.TryStart(Snapshot(0));
        Check(start != null && start.Contains("자동 예매 시작") && start.Contains("이번 실행 추가 조회 시도: 0회"), "start includes zero additional query attempts");
        Check(start.Contains("서울 → 부산") && start.Contains("KTX 101, 103") && start.Contains("예약 순서: 1 / 2"), "start includes configured target and queue position");
        Check(start.Contains("진행 단계: 좌석 확인") && start.Contains("조건에 맞는 좌석"), "start includes observed stage and situation");
        Check(start.Contains("추가 조회 시도 50회마다"), "start explains the frozen reporting interval");
        Check(start.Contains("예약 확정 알림이 아니며") && start.Contains("결제를 진행하지 않습니다"), "status report does not claim a booking or payment");
        Check(session.TryStart(Snapshot(20)) == null, "duplicate start is suppressed");
        Check(session.TryProgress(Snapshot(0)) == null, "zero queries is not a progress milestone");
        Check(session.TryProgress(Snapshot(49)) == null, "49 additional query attempts do not reach a 50 query milestone");
        string progress = session.TryProgress(Snapshot(50));
        Check(progress != null && progress.Contains("중간 진행 보고") && progress.Contains("이번 실행 추가 조회 시도: 50회"), "50 additional query attempts produce one progress report");
        Check(session.TryProgress(Snapshot(50)) == null && session.TryProgress(Snapshot(99)) == null, "same milestone and its remaining bucket do not repeat");
        Check(session.TryProgress(Snapshot(100)) != null, "next interval produces a report");
        Check(session.TryProgress(Snapshot(250)) != null && session.TryProgress(Snapshot(250)) == null, "jump across intervals emits only one current report");
        Check(session.TryProgress(Snapshot(0)) == null && session.TryProgress(Snapshot(50)) == null, "backwards query counters never rearm old thresholds");
        Check(session.TryProgress(Snapshot(299)) == null && session.TryProgress(Snapshot(300)) != null, "progress resumes only above the high water milestone");
        string error = session.TryError(Snapshot(7), Failure());
        Check(error != null && error.Contains("이번 실행 추가 조회 시도: 300회"), "terminal error preserves the largest observed additional query attempt count");
        Check(error.Contains("BROWSER_EXIT") && error.Contains("브라우저 프로세스가 종료") && error.Contains("다시 실행하고 로그인"), "error includes the observed reason and concrete action");
        Check(!error.Contains("예약 결과 미확인:"), "ordinary errors do not invent an unconfirmed submission");
        Check(session.TryError(Snapshot(301), Failure()) == null, "terminal error is sent at most once");
        Check(session.TryProgress(Snapshot(500)) == null && session.TryStart(Snapshot(0)) == null, "terminal error prevents progress or restart in the same session");

        var stopped = new AutomationReportSession(Settings());
        stopped.TryStart(Snapshot(0)); stopped.Stop();
        Check(stopped.TryProgress(Snapshot(50)) == null, "stopped sessions reject late progress callbacks");
        Check(stopped.TryError(Snapshot(50), Failure()) == null, "stopped sessions reject late errors from obsolete runs");
        Check(stopped.TryStart(Snapshot(0)) == null, "stopped sessions cannot restart");
        var preStopped = new AutomationReportSession(Settings()); preStopped.Stop();
        Check(preStopped.TryStart(Snapshot(0)) == null, "stopping before start suppresses all start notifications");
        var earlyError = new AutomationReportSession(Settings());
        Check(earlyError.TryError(Snapshot(0), Failure()) != null && earlyError.TryStart(Snapshot(0)) == null, "startup failures can report before the first query and remain terminal");

        var unknown = Failure(); unknown.ReservationResultUnknown = true;
        string uncertain = new AutomationReportSession(Settings()).TryError(Snapshot(1), unknown);
        Check(uncertain.Contains("예약 결과 미확인") && uncertain.Contains("재시도하기 전에 코레일 공식 예약 내역") && uncertain.Contains("중복 예약"), "unknown post-submit result requires official booking review before retry");
        string unknownError = new AutomationReportSession(Settings()).TryError(null, null);
        Check(unknownError.Contains("UNKNOWN") && unknownError.Contains("확인된 상세 이유가 없습니다"), "missing diagnostics do not invent a technical cause");
        Check(unknownError.Contains("원인을 알 수 없으면") && unknownError.Contains("이번 실행 추가 조회 시도: 0회"), "missing snapshot and action have safe explicit defaults");

        var disabledSettings = Settings(); disabledSettings.Enabled = false;
        var disabled = new AutomationReportSession(disabledSettings);
        Check(disabled.TryStart(Snapshot(0)) == null && disabled.TryProgress(Snapshot(50)) == null && disabled.TryError(Snapshot(50), Failure()) == null, "master notification toggle disables every report");
        var noStartSettings = Settings(); noStartSettings.StartNotificationsEnabled = false;
        var noStart = new AutomationReportSession(noStartSettings);
        Check(noStart.TryStart(Snapshot(0)) == null && noStart.TryProgress(Snapshot(50)) != null, "start toggle does not prevent requested progress reports");
        var noProgressSettings = Settings(); noProgressSettings.ProgressNotificationsEnabled = false;
        var noProgress = new AutomationReportSession(noProgressSettings);
        Check(noProgress.TryStart(Snapshot(0)) != null && noProgress.TryProgress(Snapshot(50)) == null && noProgress.TryError(Snapshot(50), Failure()) != null, "progress toggle leaves start and error independent");
        var noErrorSettings = Settings(); noErrorSettings.ErrorNotificationsEnabled = false;
        var noError = new AutomationReportSession(noErrorSettings); noError.TryStart(Snapshot(0));
        Check(noError.TryError(Snapshot(1), Failure()) == null && noError.TryProgress(Snapshot(50)) == null, "suppressed terminal error still prevents later stale progress");
        var absent = new AutomationReportSession(null);
        Check(absent.TryStart(null) == null && absent.TryError(null, null) == null, "missing settings cannot enable notifications");

        var changedSettings = Settings(); var copied = new AutomationReportSession(changedSettings);
        changedSettings.Enabled = false; changedSettings.StartNotificationsEnabled = false;
        changedSettings.ErrorNotificationsEnabled = false; changedSettings.ProgressNotificationsEnabled = false;
        changedSettings.ProgressEveryQueries = 1; changedSettings.BotToken = "changed"; changedSettings.ChatId = "changed";
        Check(copied.TryStart(Snapshot(0)) != null && copied.TryProgress(Snapshot(1)) == null && copied.TryProgress(Snapshot(50)) != null, "live settings changes cannot alter a session's copied preferences or cadence");
        var secretSnapshot = Snapshot(51); secretSnapshot.Status = FakeToken + " " + FakeChatId;
        string copiedError = copied.TryError(secretSnapshot, Failure());
        Check(copiedError != null && !copiedError.Contains(FakeToken) && !copiedError.Contains(FakeChatId), "copied credentials stay redacted after settings mutation");

        var oneSettings = Settings(); oneSettings.ProgressEveryQueries = 1;
        var one = new AutomationReportSession(oneSettings); one.TryStart(Snapshot(-20));
        Check(one.TryProgress(Snapshot(-1)) == null && one.TryProgress(Snapshot(0)) == null && one.TryProgress(Snapshot(1)) != null, "negative counts never trigger while interval one works");
        var invalidIntervalSettings = Settings(); invalidIntervalSettings.ProgressEveryQueries = 0;
        var invalidInterval = new AutomationReportSession(invalidIntervalSettings); invalidInterval.TryStart(Snapshot(0));
        Check(invalidInterval.TryProgress(Snapshot(49)) == null && invalidInterval.TryProgress(Snapshot(50)) != null, "invalid interval safely defaults to 50 without division by zero");
        var baseline = new AutomationReportSession(Settings()); baseline.TryStart(Snapshot(150));
        Check(baseline.TryProgress(Snapshot(150)) == null && baseline.TryProgress(Snapshot(199)) == null && baseline.TryProgress(Snapshot(200)) != null, "a restored nonzero start establishes a baseline without duplicate historical reports");
        var huge = new AutomationReportSession(oneSettings); huge.TryStart(Snapshot(0));
        string hugeMessage = huge.TryProgress(Snapshot(Int64.MaxValue));
        Check(hugeMessage != null && hugeMessage.Contains(Int64.MaxValue.ToString(CultureInfo.InvariantCulture)), "Int64 maximum additional query attempt count formats without arithmetic overflow");
        Check(huge.TryProgress(Snapshot(Int64.MaxValue)) == null && huge.TryProgress(Snapshot(Int64.MinValue)) == null, "maximum and wrapped counters cannot rearm notifications");
        var bigIntervalSettings = Settings(); bigIntervalSettings.ProgressEveryQueries = Int32.MaxValue;
        var bigInterval = new AutomationReportSession(bigIntervalSettings); bigInterval.TryStart(Snapshot(0));
        Check(bigInterval.TryProgress(Snapshot((long)Int32.MaxValue - 1)) == null && bigInterval.TryProgress(Snapshot(Int64.MaxValue)) != null, "large interval and jumped long counter use overflow-free division");

        var noQueue = Snapshot(0); noQueue.JobIndex = 3; noQueue.JobCount = 2;
        Check(!new AutomationReportSession(Settings()).TryStart(noQueue).Contains("예약 순서:"), "invalid queue position is omitted instead of invented");
        var nullFields = new AutomationReportSnapshot();
        string empty = new AutomationReportSession(Settings()).TryStart(nullFields);
        Check(empty.Contains("진행 단계: 확인 중") && !empty.Contains("열차 조건:"), "empty optional fields do not create fake target information");

        var privacy = Snapshot(0);
        string otherToken = "987654321:abcdefghijklmnopqrstuvwxyz0123456789-";
        privacy.TargetSummary = "열차 " + FakeToken + " " + FakeChatId + " " + otherToken;
        privacy.Status = "https://api.telegram.org/bot" + FakeToken + "/sendMessage?chat_id=" + FakeChatId + "\npassword: never-share-this\n";
        privacy.Stage = "C:\\Users\\private-user\\secrets.json";
        var privacyError = Failure();
        privacyError.ObservedReason = "Observed failure\n   at Demo.Secret(password) in C:\\Users\\private-user\\source.cs:line 10\n비밀번호=another-secret";
        privacyError.RequiredAction = "chat_id: " + FakeChatId + "\nbot_token: " + otherToken;
        string sanitized = new AutomationReportSession(Settings()).TryError(privacy, privacyError);
        Check(!sanitized.Contains(FakeToken) && !sanitized.Contains(otherToken) && !sanitized.Contains(FakeChatId), "token patterns and configured chat ID are removed from every field");
        Check(!sanitized.Contains("never-share-this") && !sanitized.Contains("another-secret") && !sanitized.Contains("Demo.Secret"), "password labels and stack frames are removed");
        Check(!sanitized.Contains("api.telegram.org") && !sanitized.Contains("private-user") && !sanitized.Contains("secrets.json"), "URLs and local paths cannot leak diagnostic internals");
        var encoded = Snapshot(0); encoded.Status = "987654321%3Aabcdefghijklmnopqrstuvwxyz0123456789-";
        Check(!new AutomationReportSession(Settings()).TryStart(encoded).Contains("abcdefghijklmnopqrstuvwxyz0123456789"), "encoded token with a dash ending is redacted");
        var longToken = Snapshot(0); longToken.Status = "987654321:" + new String('Z', 300);
        Check(!new AutomationReportSession(Settings()).TryStart(longToken).Contains(new String('Z', 20)), "oversized token-shaped secrets are fully redacted rather than partly truncated");
        var controls = Snapshot(0); controls.Status = "status\r\nforged field:\tvalue\0\u202Ehidden\uD800bad\uDC00";
        string controlMessage = new AutomationReportSession(Settings()).TryStart(controls);
        Check(controlMessage.Contains("현재 상황: status forged field: value hiddenbad") && !controlMessage.Contains("\nforged"), "untrusted control characters cannot create extra report fields");
        Check(WellFormed(controlMessage) && !controlMessage.Contains("\u202E"), "unpaired surrogates and directional formatting are removed");

        var longSnapshot = Snapshot(Int64.MaxValue);
        longSnapshot.TargetSummary = new String('열', 16000); longSnapshot.Stage = new String('단', 16000);
        longSnapshot.Status = new String('상', 16000);
        var longError = new AutomationReportError { Code = new String('C', 16000), Category = new String('종', 16000),
            ObservedReason = new String('이', 16000), RequiredAction = new String('조', 16000), ReservationResultUnknown = true };
        string bounded = new AutomationReportSession(Settings()).TryError(longSnapshot, longError);
        Check(bounded.Length <= 4000 && bounded.Contains("예약 결과 미확인") && bounded.Contains("결제를 진행하지 않습니다"), "bounded fields retain all required cautions inside Telegram's message limit");
        Check(bounded.Contains("…"), "truncated diagnostics visibly indicate omitted text");
        longSnapshot.Status = new String('x', 20000) + FakeToken;
        Check(new AutomationReportSession(Settings()).TryStart(longSnapshot).Contains("[긴 진단 내용 생략]"), "oversized diagnostics are discarded before any partial credential truncation");
        longSnapshot.TargetSummary = new String('x', 478) + "\uD83D\uDE80" + "extra";
        string unicode = new AutomationReportSession(Settings()).TryStart(longSnapshot);
        Check(WellFormed(unicode), "truncating a field at a surrogate boundary preserves valid Unicode");
    }
}
