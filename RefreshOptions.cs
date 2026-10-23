using System;
using System.Globalization;

internal enum BookingCompletionMode
{
    Any = 0,
    All = 1
}

// Shared by settings preview, settings acceptance and automation startup.
// Text is validated before conversion so no control can silently coerce it.
internal sealed class RefreshOptions
{
    public int IntervalSeconds { get; private set; }
    public int Limit { get; private set; }
    public int JitterPercent { get; private set; }
    public bool Unlimited { get; private set; }
    public BookingCompletionMode CompletionMode { get; private set; }

    public RefreshOptions(int intervalSeconds, int limit, int jitterPercent)
        : this(intervalSeconds, limit, jitterPercent, false, BookingCompletionMode.Any)
    {
    }

    public RefreshOptions(int intervalSeconds, int limit, int jitterPercent, bool unlimited, BookingCompletionMode completionMode)
    {
        if (intervalSeconds < 1)
            throw new ArgumentOutOfRangeException("intervalSeconds", "조회 간격은 1~2147483647초의 정수로 입력하세요.");
        if (limit < 1)
            throw new ArgumentOutOfRangeException("limit", "최대 조회 횟수는 1~2147483647회의 정수로 입력하세요.");
        if (jitterPercent < 0 || jitterPercent > 100)
            throw new ArgumentOutOfRangeException("jitterPercent", "간격 변동 폭은 0~100%의 정수로 입력하세요.");
        if (completionMode != BookingCompletionMode.Any && completionMode != BookingCompletionMode.All)
            throw new ArgumentOutOfRangeException("completionMode", "예매 완료 조건을 선택하세요.");
        IntervalSeconds = intervalSeconds;
        Limit = limit;
        JitterPercent = jitterPercent;
        Unlimited = unlimited;
        CompletionMode = completionMode;
    }

    static bool TryNumber(string text, int minimum, int maximum, out int value)
    {
        value = 0;
        if (text == null) return false;
        text = text.Trim();
        if (text.Length == 0) return false;
        foreach (char digit in text)
            if (digit < '0' || digit > '9') return false;
        return Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum;
    }

    public static bool TryParse(string intervalText, string countText, string jitterText,
        out RefreshOptions options, out string error)
    {
        return TryParse(intervalText, countText, jitterText, false, BookingCompletionMode.Any, out options, out error);
    }

    public static bool TryParse(string intervalText, string countText, string jitterText, bool unlimited,
        BookingCompletionMode completionMode, out RefreshOptions options, out string error)
    {
        options = null;
        error = null;
        int interval, limit, jitter;
        if (!TryNumber(intervalText, 1, Int32.MaxValue, out interval))
        {
            error = "조회 간격은 1~2147483647초의 정수로 입력하세요. 입력값은 자동 보정되지 않습니다.";
            return false;
        }
        if (!TryNumber(countText, 1, Int32.MaxValue, out limit))
        {
            if (!unlimited)
            {
                error = "최대 조회 횟수는 1~2147483647회의 정수로 입력하세요. 입력값은 자동 보정되지 않습니다.";
                return false;
            }
            // The disabled count is dormant, never a sentinel for unlimited execution.
            limit = 1;
        }
        if (!TryNumber(jitterText, 0, 100, out jitter))
        {
            error = "간격 변동 폭은 0~100%의 정수로 입력하세요. 입력값은 자동 보정되지 않습니다.";
            return false;
        }
        if (completionMode != BookingCompletionMode.Any && completionMode != BookingCompletionMode.All)
        {
            error = "예매 완료 조건을 선택하세요.";
            return false;
        }
        options = new RefreshOptions(interval, limit, jitter, unlimited, completionMode);
        return true;
    }
}
