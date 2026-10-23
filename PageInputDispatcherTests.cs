using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Protocol transport only. No browser, native input, network, or reservations.
internal static class PageInputDispatcherTests
{
    sealed class Call
    {
        public string Method;
        public Dictionary<string, object> Data;
    }
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int count;
    static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
    static bool Text(Call call, string key, string expected)
    { return call.Data.ContainsKey(key) && Object.Equals(call.Data[key], expected); }
    static bool Number(Call call, string key, double expected)
    { return call.Data.ContainsKey(key) && Convert.ToDouble(call.Data[key]) == expected; }
    static Call Read(string method, string payload)
    { return new Call { Method = method, Data = Json.Deserialize<Dictionary<string, object>>(payload) }; }
    static int Main()
    {
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("Passed " + count + " page input dispatcher checks (mock protocol only)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static async Task Run()
    {
        var calls = new List<Call>(); int validations = 0;
        var transport = new PageInputDispatcher((method, payload) => { calls.Add(Read(method, payload)); return Task.FromResult("{}"); }, () => validations++);
        await transport.ClickAsync(12.25, 44.5, 1024, 768);
        Check(calls.Count == 2 && validations == 2, "click validates each of its two ordered commands");
        Check(calls[0].Method == "Input.dispatchMouseEvent" && Text(calls[0], "type", "mousePressed") && calls[1].Method == "Input.dispatchMouseEvent" && Text(calls[1], "type", "mouseReleased"), "click dispatches one down then one up");
        Check(Number(calls[0], "x", 12.25) && Number(calls[0], "y", 44.5) && Number(calls[1], "x", 12.25) && Number(calls[1], "y", 44.5), "fractional CSS coordinates are retained without screen conversion");
        Check(Text(calls[0], "button", "left") && Text(calls[1], "button", "left") && Number(calls[0], "buttons", 1) && Number(calls[1], "buttons", 0) && Number(calls[0], "clickCount", 1) && Number(calls[1], "clickCount", 1), "click explicitly declares left button state and single click");
        Check(Number(calls[0], "modifiers", 0) && Number(calls[1], "modifiers", 0), "click never inherits physical modifier keys from another app");
        calls.Clear(); validations = 0;
        await transport.WheelAsync(300, 400, 1024, 768, 360);
        await transport.WheelAsync(300, 400, 1024, 768, -360);
        Check(calls.Count == 2 && validations == 2 && Text(calls[0], "type", "mouseWheel") && Number(calls[0], "deltaY", -360) && Number(calls[1], "deltaY", 360), "wheel preserves up and down after converting Windows sign");
        Check(Number(calls[0], "deltaX", 0) && Number(calls[0], "buttons", 0) && Number(calls[0], "modifiers", 0) && Text(calls[0], "button", "none"), "wheel does not invent a drag or horizontal scroll");
        calls.Clear(); validations = 0;
        await transport.ReplaceStationTextAsync("서울");
        Check(calls.Count == 3 && validations == 3 && calls[0].Method == "Input.dispatchKeyEvent" && calls[1].Method == "Input.dispatchKeyEvent" && calls[2].Method == "Input.insertText", "station replacement validates ordered select-all release and text commands");
        Check(Text(calls[0], "type", "rawKeyDown") && Text(calls[1], "type", "keyUp") && Text(calls[0], "key", "a") && Text(calls[0], "code", "KeyA") && Number(calls[0], "windowsVirtualKeyCode", 65) && Number(calls[0], "modifiers", 2), "select all targets the page with Ctrl+A");
        Check(Number(calls[1], "modifiers", 0) && Text(calls[1], "key", "a") && Number(calls[1], "windowsVirtualKeyCode", 65), "key release clears the page modifier state");
        Check(Text(calls[2], "text", "서울") && calls[2].Data.Count == 1, "Unicode station text uses the protocol text field only");
        calls.Clear(); await transport.ReplaceStationTextAsync("역\"\\명");
        Check(Text(calls[2], "text", "역\"\\명"), "JSON escaping preserves quote and backslash without executable interpolation");

        foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, -1.0, 1024.0 })
        {
            calls.Clear(); double value = invalid;
            await Throws<ArgumentOutOfRangeException>(() => transport.ClickAsync(value, 10, 1024, 768), "invalid horizontal coordinate rejected: " + value);
            Check(calls.Count == 0, "invalid horizontal coordinate sends no input: " + value);
        }
        foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, -1.0, 768.0 })
        {
            calls.Clear(); double value = invalid;
            await Throws<ArgumentOutOfRangeException>(() => transport.WheelAsync(10, value, 1024, 768, 360), "invalid vertical coordinate rejected: " + value);
            Check(calls.Count == 0, "invalid wheel coordinate sends no input: " + value);
        }
        foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, 0.0, -1.0, 32769.0 })
        {
            double value = invalid; calls.Clear();
            await Throws<ArgumentOutOfRangeException>(() => transport.ClickAsync(1, 1, value, 768), "invalid viewport width rejected: " + value);
            await Throws<ArgumentOutOfRangeException>(() => transport.ClickAsync(1, 1, 1024, value), "invalid viewport height rejected: " + value);
            Check(calls.Count == 0, "invalid viewport sends no input: " + value);
        }
        foreach (int invalid in new[] { 0, Int32.MinValue, Int32.MaxValue, -1201, 1201 })
        {
            int value = invalid; calls.Clear();
            await Throws<ArgumentOutOfRangeException>(() => transport.WheelAsync(10, 10, 1024, 768, value), "invalid wheel delta rejected: " + value);
            Check(calls.Count == 0, "invalid wheel delta sends no input: " + value);
        }
        foreach (string invalid in new[] { null, "", " ", "서울\n", "서\t울", "서\0울", new string('가', 41) })
        {
            string value = invalid; calls.Clear();
            await Throws<ArgumentException>(() => transport.ReplaceStationTextAsync(value), "invalid station rejected");
            Check(calls.Count == 0, "invalid station sends no input");
        }

        calls.Clear(); validations = 0;
        var first = new TaskCompletionSource<string>();
        var ordered = new PageInputDispatcher((method, payload) => { calls.Add(Read(method, payload)); return calls.Count == 1 ? first.Task : Task.FromResult("{}"); }, () => validations++);
        Task pending = ordered.ClickAsync(20, 20, 100, 100);
        Check(!pending.IsCompleted && calls.Count == 1 && validations == 1, "mouse release waits for the pressed command acknowledgement");
        await Throws<InvalidOperationException>(() => ordered.WheelAsync(20, 20, 100, 100, 120), "overlapping operation rejected instead of interleaving its inputs");
        Check(calls.Count == 1, "rejected overlap sends no command");
        first.SetResult("{}"); await pending;
        Check(calls.Count == 2 && validations == 2, "release dispatches after the first acknowledgement");

        calls.Clear(); validations = 0;
        var stopped = new PageInputDispatcher((method, payload) => { calls.Add(Read(method, payload)); return Task.FromResult("{}"); }, () => { if (++validations == 2) throw new OperationCanceledException("stopped"); });
        await Throws<OperationCanceledException>(() => stopped.ClickAsync(20, 20, 100, 100), "stop before second click command cancels it");
        Check(calls.Count == 1 && Text(calls[0], "type", "mousePressed"), "cancelled click has no release, retry or native fallback");
        calls.Clear(); validations = 0;
        var stoppedText = new PageInputDispatcher((method, payload) => { calls.Add(Read(method, payload)); return Task.FromResult("{}"); }, () => { if (++validations == 3) throw new OperationCanceledException("navigation changed"); });
        await Throws<OperationCanceledException>(() => stoppedText.ReplaceStationTextAsync("전주"), "stop before text insertion cancels it");
        Check(calls.Count == 2 && calls.TrueForAll(call => call.Method == "Input.dispatchKeyEvent"), "changed document never receives station text");

        calls.Clear(); validations = 0;
        var failed = new TaskCompletionSource<string>(); failed.SetException(new InvalidOperationException("transport failed"));
        var failure = new PageInputDispatcher((method, payload) => { calls.Add(Read(method, payload)); return failed.Task; }, () => validations++);
        await Throws<InvalidOperationException>(() => failure.ClickAsync(20, 20, 100, 100), "transport failure propagates");
        Check(calls.Count == 1 && validations == 1, "transport failure sends no release or retry");
        calls.Clear(); validations = 0;
        var failedRelease = new PageInputDispatcher((method, payload) => { calls.Add(Read(method, payload)); return calls.Count == 1 ? Task.FromResult("{}") : failed.Task; }, () => validations++);
        await Throws<InvalidOperationException>(() => failedRelease.ClickAsync(20, 20, 100, 100), "release transport failure propagates");
        Check(calls.Count == 2 && validations == 2, "uncertain click result is never replayed");
        await Throws<InvalidOperationException>(() => new PageInputDispatcher((method, payload) => null, () => { }).WheelAsync(10, 10, 100, 100, 120), "missing transport task fails closed");
        await Throws<ArgumentNullException>(() => { new PageInputDispatcher(null, () => { }); return Task.FromResult(0); }, "missing protocol endpoint rejected");
        await Throws<ArgumentNullException>(() => { new PageInputDispatcher((method, payload) => Task.FromResult("{}"), null); return Task.FromResult(0); }, "missing guard callback rejected");
    }
}
