using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Host observations only. This policy cannot inspect a browser, send a message,
// or decide that a reservation succeeded.
internal sealed class AutomationReportSnapshot
{
    public long QueryCount;
    public int JobIndex, JobCount;
    public string Stage, Status, TargetSummary;
}

internal sealed class AutomationReportError
{
    public string Code, Category, ObservedReason, RequiredAction;
    public bool ReservationResultUnknown;
}

internal sealed class AutomationReportSession
{
    readonly bool enabled, startEnabled, progressEnabled, errorEnabled;
    readonly long interval;
    readonly string botToken, chatId;
    bool started, stopped, terminalError;
    long highWater, reportedBucket;

    public AutomationReportSession(TelegramSettings settings)
    {
        // Copy scalar values: changing the settings window cannot alter an active
        // run's destination, cadence or notification preferences.
        enabled = settings != null && settings.Enabled;
        startEnabled = settings != null && settings.StartNotificationsEnabled;
        progressEnabled = settings != null && settings.ProgressNotificationsEnabled;
        errorEnabled = settings != null && settings.ErrorNotificationsEnabled;
        interval = settings == null || settings.ProgressEveryQueries < 1 ? 50L : settings.ProgressEveryQueries;
        botToken = settings == null ? "" : (settings.BotToken ?? "").Trim();
        chatId = settings == null ? "" : (settings.ChatId ?? "").Trim();
    }

    public string TryStart(AutomationReportSnapshot snapshot)
    {
        if (started || stopped || terminalError) return null;
        started = true;
        Observe(snapshot);
        reportedBucket = highWater / interval;
        if (!enabled || !startEnabled) return null;
        return Format("자동 예매 시작", snapshot, null);
    }

    public string TryProgress(AutomationReportSnapshot snapshot)
    {
        if (!started || stopped || terminalError) return null;
        Observe(snapshot);
        long bucket = highWater / interval;
        if (highWater == 0 || bucket <= reportedBucket) return null;
        // Division avoids overflow at Int64.MaxValue. A large jump produces one
        // current report, never one message for each missed interval.
        reportedBucket = bucket;
        if (!enabled || !progressEnabled) return null;
        return Format("중간 진행 보고", snapshot, null);
    }

    public string TryError(AutomationReportSnapshot snapshot, AutomationReportError error)
    {
        if (stopped || terminalError) return null;
        terminalError = true;
        Observe(snapshot);
        if (!enabled || !errorEnabled) return null;
        return Format("자동 예매 오류로 중지", snapshot, error ?? new AutomationReportError());
    }

    public void Stop() { stopped = true; }

    void Observe(AutomationReportSnapshot snapshot)
    {
        if (snapshot != null && snapshot.QueryCount > highWater) highWater = snapshot.QueryCount;
    }

    string Format(string title, AutomationReportSnapshot snapshot, AutomationReportError error)
    {
        snapshot = snapshot ?? new AutomationReportSnapshot();
        var text = new StringBuilder("코레일 도우미 · " + title);
        text.Append("\n이번 실행 추가 조회 시도: ").Append(highWater.ToString(CultureInfo.InvariantCulture)).Append("회");
        if (snapshot.JobIndex > 0 && snapshot.JobCount > 0 && snapshot.JobIndex <= snapshot.JobCount)
            text.Append("\n예약 순서: ").Append(snapshot.JobIndex.ToString(CultureInfo.InvariantCulture))
                .Append(" / ").Append(snapshot.JobCount.ToString(CultureInfo.InvariantCulture));
        Add(text, "열차 조건", snapshot.TargetSummary, 480, "");
        Add(text, "진행 단계", snapshot.Stage, 80, "확인 중");
        Add(text, "현재 상황", snapshot.Status, 480, "상세 상태를 확인 중입니다.");
        if (error == null)
        {
            if (progressEnabled) text.Append("\n중간 보고 간격: 추가 조회 시도 ").Append(interval.ToString(CultureInfo.InvariantCulture)).Append("회마다 (결과 확인 후 보고)");
        }
        else
        {
            Add(text, "오류 코드", error.Code, 80, "UNKNOWN");
            Add(text, "오류 종류", error.Category, 120, "확인 필요");
            Add(text, "확인된 이유", error.ObservedReason, 640, "확인된 상세 이유가 없습니다.");
            Add(text, "필요한 조치", error.RequiredAction, 640,
                "현재 화면의 안내를 확인하세요. 원인을 알 수 없으면 도우미를 중지한 상태로 오류 내용을 기록해 문의하세요.");
            if (error.ReservationResultUnknown)
                text.Append("\n예약 결과 미확인: 다시 시작하거나 예매를 재시도하기 전에 코레일 공식 예약 내역에서 해당 열차의 예약 여부를 먼저 확인하세요. 중복 예약을 피해야 합니다.");
        }
        text.Append("\n이 보고는 예약 확정 알림이 아니며, 도우미는 결제를 진행하지 않습니다.");
        // Field limits leave room for every label and the mandatory result note.
        return Bound(text.ToString(), 4000);
    }

    void Add(StringBuilder result, string label, string value, int limit, string fallback)
    {
        string safe = Safe(value, limit);
        if (safe.Length == 0) safe = fallback;
        if (safe.Length > 0) result.Append("\n").Append(label).Append(": ").Append(safe);
    }

    string Safe(string value, int limit)
    {
        if (String.IsNullOrEmpty(value)) return "";
        // Reject oversized input instead of cutting a credential in half before
        // redaction. Diagnostics should be fixed descriptions, never exception dumps.
        if (value.Length > 16384) return "[긴 진단 내용 생략]";
        if (botToken.Length > 0) value = value.Replace(botToken, "[봇 토큰 숨김]");
        try
        {
            if (chatId.Length > 0)
                value = Replace(value, @"(?<![0-9])" + Regex.Escape(chatId) + @"(?![0-9])", "[Chat ID 숨김]");
            value = Replace(value, @"(?<![0-9])[0-9]{5,20}(?::|%3[Aa])[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])", "[봇 토큰 숨김]");
            value = Replace(value, @"(?im)^\s*(?:at\s+|위치:\s*).*$", "[호출 정보 생략]");
            value = Replace(value, @"(?i)(?:https?://|file:///)[^\s<>]+", "[주소 숨김]");
            value = Replace(value, @"(?i)[A-Z]:[\\/][^\r\n]*", "[로컬 경로 숨김]");
            value = Replace(value, @"(?im)(?:password|passwd|pwd|비밀번호|bot[_ ]?token|봇\s*토큰|chat[_ ]?id)\s*[:=]\s*[^\r\n]+", "[연결 정보 숨김]");
        }
        catch (RegexMatchTimeoutException) { return "[진단 내용 생략]"; }
        var clean = new StringBuilder();
        bool space = false;
        for (int i = 0; i < value.Length; i++)
        {
            char item = value[i];
            if (Char.IsWhiteSpace(item) || Char.IsControl(item)) { space = clean.Length > 0; continue; }
            if (Char.IsSurrogate(item))
            {
                if (!Char.IsHighSurrogate(item) || i + 1 >= value.Length || !Char.IsLowSurrogate(value[i + 1])) continue;
                if (space) { clean.Append(' '); space = false; }
                clean.Append(item).Append(value[++i]); continue;
            }
            // Directional controls and invisible format characters must not disguise fields.
            if (Char.GetUnicodeCategory(item) == UnicodeCategory.Format) continue;
            if (space) { clean.Append(' '); space = false; }
            clean.Append(item);
        }
        return Bound(clean.ToString(), limit);
    }

    static string Replace(string value, string pattern, string replacement)
    { return Regex.Replace(value, pattern, replacement, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }

    static string Bound(string value, int limit)
    {
        if (value.Length <= limit) return value;
        int length = limit - 1;
        if (length > 0 && Char.IsHighSurrogate(value[length - 1])) length--;
        return value.Substring(0, length) + "…";
    }
}
