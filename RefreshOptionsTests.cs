using System;

internal static class RefreshOptionsTests
{
    static int checks;
    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static void Valid(string interval, string count, string jitter, int expectedInterval, int expectedCount, int expectedJitter)
    {
        RefreshOptions options; string error;
        Check(RefreshOptions.TryParse(interval, count, jitter, out options, out error) && error == null && options != null &&
            options.IntervalSeconds == expectedInterval && options.Limit == expectedCount && options.JitterPercent == expectedJitter,
            "exact parsed options: " + interval + "/" + count + "/" + jitter);
    }
    static void Invalid(string interval, string count, string jitter, string field, string name)
    {
        RefreshOptions options; string error;
        Check(!RefreshOptions.TryParse(interval, count, jitter, out options, out error) && options == null &&
            !String.IsNullOrEmpty(error) && error.Contains(field), "rejects " + name);
    }
    static void ConstructorRejects(int interval, int count, int jitter, string parameter)
    {
        bool rejected = false;
        try { new RefreshOptions(interval, count, jitter); }
        catch (ArgumentOutOfRangeException error) { rejected = error.ParamName == parameter; }
        Check(rejected, "startup rejects invalid " + parameter + ": " + interval + "/" + count + "/" + jitter);
    }
    static int Main()
    {
        try
        {
            Valid("5", "999999", "0", 5, 999999, 0);
            Valid("5", "9999999", "50", 5, 9999999, 50);
            Valid("1", "1", "100", 1, 1, 100);
            Valid("2147483647", "2147483647", "100", Int32.MaxValue, Int32.MaxValue, 100);
            Valid(" 005 ", "\t000999999\r\n", " 00 ", 5, 999999, 0);
            Valid("301", "21", "1", 301, 21, 1);

            string[] invalidPositive = { null, "", " ", "0", "-1", "+5", "5.0", "5,0", "5e0", "1 0", "１", "١", "2147483648", "9999999999999999999999999999999", "5\n6" };
            foreach (string value in invalidPositive)
            {
                Invalid(value, "999999", "0", "조회 간격", "interval " + (value ?? "null"));
                Invalid("5", value, "0", "조회 횟수", "count " + (value ?? "null"));
            }
            foreach (string value in new [] { null, "", " ", "-1", "+50", "50.0", "1e2", "101", "2147483648", "５", "5 0" })
                Invalid("5", "999999", value, "변동 폭", "jitter " + (value ?? "null"));
            foreach (int value in new [] { Int32.MinValue, -1, 0 })
            {
                ConstructorRejects(value, 999999, 0, "intervalSeconds");
                ConstructorRejects(5, value, 0, "limit");
            }
            foreach (int value in new [] { Int32.MinValue, -1, 101, Int32.MaxValue })
                ConstructorRejects(5, 999999, value, "jitterPercent");
            var maximum = new RefreshOptions(Int32.MaxValue, Int32.MaxValue, 100);
            Check(maximum.IntervalSeconds == Int32.MaxValue && maximum.Limit == Int32.MaxValue && maximum.JitterPercent == 100,
                "startup accepts full positive Int32 range without coercion");
            Check(!maximum.Unlimited && maximum.CompletionMode == BookingCompletionMode.Any,
                "existing constructor keeps finite queries and first successful train as defaults");

            RefreshOptions options; string error;
            RefreshOptions.TryParse("5", "999999", "0", out options, out error);
            Check(!options.Unlimited && options.CompletionMode == BookingCompletionMode.Any,
                "existing parser keeps finite queries and first successful train as defaults");
            Check(!RefreshOptions.TryParse("bad", "999999", "0", out options, out error) && options == null,
                "invalid preview cannot reuse previously valid options");

            Check(RefreshOptions.TryParse("5", "87", "20", true, BookingCompletionMode.All, out options, out error) &&
                error == null && options.Unlimited && options.CompletionMode == BookingCompletionMode.All &&
                options.IntervalSeconds == 5 && options.Limit == 87 && options.JitterPercent == 20,
                "unlimited all-trains options preserve interval, dormant finite count and jitter");
            Check(RefreshOptions.TryParse("5", "87", "20", false, BookingCompletionMode.All, out options, out error) &&
                !options.Unlimited && options.CompletionMode == BookingCompletionMode.All && options.Limit == 87,
                "all-trains completion can retain a finite shared query budget");
            foreach (string value in new [] { null, "", "0", "-1", "invalid", "2147483648" })
            {
                Check(RefreshOptions.TryParse("5", value, "0", true, BookingCompletionMode.Any, out options, out error) &&
                    error == null && options.Unlimited && options.Limit == 1,
                    "unlimited execution ignores disabled count: " + (value ?? "null"));
                Check(!RefreshOptions.TryParse("5", value, "0", false, BookingCompletionMode.Any, out options, out error) &&
                    options == null && error.Contains("조회 횟수"),
                    "returning to finite execution validates the count again: " + (value ?? "null"));
            }
            Check(!RefreshOptions.TryParse("0", "invalid", "0", true, BookingCompletionMode.Any, out options, out error) &&
                options == null && error.Contains("조회 간격"),
                "unlimited execution still rejects an invalid interval");
            Check(!RefreshOptions.TryParse("5", "invalid", "101", true, BookingCompletionMode.All, out options, out error) &&
                options == null && error.Contains("변동 폭"),
                "unlimited execution still rejects an invalid jitter percentage");
            foreach (int mode in new [] { -1, 2, Int32.MaxValue })
            {
                Check(!RefreshOptions.TryParse("5", "10", "0", true, (BookingCompletionMode)mode, out options, out error) &&
                    options == null && error.Contains("완료 조건"),
                    "parser rejects an unknown booking completion mode: " + mode);
                bool rejected = false;
                try { new RefreshOptions(5, 10, 0, true, (BookingCompletionMode)mode); }
                catch (ArgumentOutOfRangeException exception) { rejected = exception.ParamName == "completionMode"; }
                Check(rejected, "startup rejects an unknown booking completion mode: " + mode);
            }
            bool invalidDormantLimit = false;
            try { new RefreshOptions(5, 0, 0, true, BookingCompletionMode.All); }
            catch (ArgumentOutOfRangeException exception) { invalidDormantLimit = exception.ParamName == "limit"; }
            Check(invalidDormantLimit, "constructor never treats a nonpositive count as an unlimited sentinel");
            Console.WriteLine("PASS " + checks + " refresh options checks (pure parsing; no UI, input or network).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
