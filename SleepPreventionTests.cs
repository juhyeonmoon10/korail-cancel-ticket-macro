using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

internal static class SleepPreventionTests
{
    static int count;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        count++;
    }

    static Exception Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    static Exception OnOtherThread(Action action)
    {
        Exception result = null;
        var thread = new Thread(delegate() { result = Capture(action); });
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(10000)) throw new Exception("FAIL: test thread did not finish.");
        return result;
    }

    static void TestNativeRequestAndRelease()
    {
        uint acquisitionObserved = 0, releaseObserved = 0;
        bool activeAfterStart = false, stopped = false, inactiveAfterStop = false;
        // Use a dedicated thread: execution-state requests belong to that thread.
        // No power plan, timeout, sleep action, or other process is changed.
        Exception error = OnOtherThread(delegate {
            uint original = SetThreadExecutionState(SleepPrevention.Continuous);
            if (original == 0) throw new Exception("Windows rejected the initial execution-state probe.");
            try
            {
                var native = new SleepPrevention();
                native.Start();
                activeAfterStart = native.Active;
                // This same-value probe returns the prior flags installed by Start.
                acquisitionObserved = SetThreadExecutionState(SleepPrevention.KeepAwake);
                stopped = native.TryStop();
                inactiveAfterStop = !native.Active;
                // Likewise, observe the state after TryStop without enabling it again.
                releaseObserved = SetThreadExecutionState(SleepPrevention.Continuous);
            }
            finally
            {
                if (SetThreadExecutionState(original) == 0)
                    throw new Exception("Windows rejected restoration of the test thread's original execution state.");
            }
        });
        if (error != null) throw new Exception("FAIL: native execution-state check: " + error.Message, error);
        Check(activeAfterStart && (acquisitionObserved & SleepPrevention.KeepAwake) == SleepPrevention.KeepAwake,
            "real Windows API reports continuous system and display requests after Start");
        Check(stopped && inactiveAfterStop && releaseObserved == SleepPrevention.Continuous,
            "real Windows API reports released system and display requests after TryStop");
    }

    static int Main()
    {
        try
        {
            var calls = new List<uint>();
            var model = new SleepPrevention(delegate(uint flags) { calls.Add(flags); return SleepPrevention.Continuous; });
            Check(!model.Active && calls.Count == 0, "default is off without calling Windows");
            Check(model.TryStop() && model.TryStop() && !model.Active && calls.Count == 0,
                "stopping an inactive instance is idempotent and makes no API request");
            model.Start();
            Check(model.Active && calls.Count == 1 && calls[0] == SleepPrevention.KeepAwake,
                "Start installs exactly the continuous system and display request");
            model.Start(); model.Start();
            Check(model.Active && calls.Count == 1, "repeated Start does not acquire again");
            Check(model.TryStop() && !model.Active && calls.Count == 2 && calls[1] == SleepPrevention.Continuous,
                "TryStop releases using continuous-only flags and clears active state");
            Check(model.TryStop() && calls.Count == 2, "repeated successful stop does not call Windows again");
            model.Start(); bool stoppedAgain = model.TryStop();
            Check(stoppedAgain && !model.Active && calls.Count == 4 && calls[2] == SleepPrevention.KeepAwake && calls[3] == SleepPrevention.Continuous,
                "a released instance can acquire and release for a later run");
            Check(Capture(delegate { new SleepPrevention(null); }) is ArgumentNullException,
                "null execution-state implementation is rejected");

            int startCalls = 0;
            var failedStart = new SleepPrevention(delegate(uint flags) { return ++startCalls == 1 ? 0U : SleepPrevention.Continuous; });
            Exception startError = Capture(failedStart.Start);
            Check(startError is InvalidOperationException && !failedStart.Active && startCalls == 1,
                "zero from Windows prevents Start and leaves the instance inactive");
            failedStart.Start();
            Check(failedStart.Active && startCalls == 2, "a failed acquisition can be retried without a stale active flag");
            failedStart.TryStop();

            int releaseCalls = 0;
            var failedRelease = new SleepPrevention(delegate(uint flags) {
                if (flags == SleepPrevention.KeepAwake) return SleepPrevention.Continuous;
                return ++releaseCalls == 1 ? 0U : SleepPrevention.KeepAwake;
            });
            failedRelease.Start();
            Check(!failedRelease.TryStop() && failedRelease.Active && releaseCalls == 1,
                "zero release result is reported and the active warning state is preserved");
            Check(failedRelease.TryStop() && !failedRelease.Active && releaseCalls == 2,
                "a later successful release clears the preserved active state");

            int threadCalls = 0;
            var owned = new SleepPrevention(delegate(uint flags) { threadCalls++; return SleepPrevention.Continuous; });
            Exception foreignStart = OnOtherThread(owned.Start);
            bool startUnchanged = !owned.Active && threadCalls == 0;
            owned.Start();
            Exception foreignStop = OnOtherThread(delegate { owned.TryStop(); });
            bool stopUnchanged = owned.Active && threadCalls == 1;
            owned.TryStop();
            Check(foreignStart is InvalidOperationException && foreignStop is InvalidOperationException && startUnchanged && stopUnchanged,
                "foreign-thread start and release are rejected before changing API or local state");

            TestNativeRequestAndRelease();
            Console.WriteLine("SleepPrevention: " + count + " checks passed (including a short native request/release). No power settings were changed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
