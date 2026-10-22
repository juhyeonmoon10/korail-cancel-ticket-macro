using System;
using System.Runtime.InteropServices;
using System.Threading;

// Execution-state requests belong to the calling thread. The host uses this
// only from its WinForms UI thread and never changes the Windows power plan.
internal sealed class SleepPrevention
{
    internal const uint Continuous = 0x80000000;
    internal const uint KeepAwake = Continuous | 0x00000001 | 0x00000002;
    readonly Func<uint, uint> setState;
    readonly int ownerThread;
    public bool Active { get; private set; }

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    public SleepPrevention() : this(SetThreadExecutionState) { }
    internal SleepPrevention(Func<uint, uint> setState)
    {
        if (setState == null) throw new ArgumentNullException("setState");
        this.setState = setState;
        ownerThread = Thread.CurrentThread.ManagedThreadId;
    }

    public void Start()
    {
        CheckThread();
        if (Active) return;
        if (setState(KeepAwake) == 0)
            throw new InvalidOperationException("절전 방지를 켜지 못해 자동 실행을 시작하지 않았습니다.");
        Active = true;
    }

    public bool TryStop()
    {
        CheckThread();
        if (!Active) return true;
        // Do not report success if Windows rejected the release. The host
        // keeps a warning visible and retries when stopping/closing again.
        if (setState(Continuous) == 0) return false;
        Active = false;
        return true;
    }

    void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != ownerThread)
            throw new InvalidOperationException("절전 방지는 시작한 UI 스레드에서만 변경할 수 있습니다.");
    }
}
