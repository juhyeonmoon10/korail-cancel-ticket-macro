using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Foreground-only input for a user-started, single reservation attempt.
// This class never activates a window, retries a failed input, or restores the
// cursor. Callers must obtain fresh visible control coordinates before calling.
internal static class NativeMouseInput
{
    const uint InputMouse = 0, InputKeyboard = 1;
    const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
    const uint MouseWheel = 0x0800, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;
    const uint KeyUp = 0x0002, RootWindow = 2;
    const int VkEnter = 0x0D, VkF8 = 0x77;
    const int SmXVirtualScreen = 76, SmYVirtualScreen = 77;
    const int SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    const int WhKeyboardLl = 13, WhMouseLl = 14;
    const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;

    // A process-lifetime provenance tag distinguishes this helper's input from
    // user input. It never alters input cadence or conceals injected input flags.
    static readonly UIntPtr InputMarker = new UIntPtr(BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0) | 1U);
    static readonly object WatchLock = new object();
    static readonly HookProc MouseHookCallback = ObserveMouse;
    static readonly HookProc KeyboardHookCallback = ObserveKeyboard;
    static IntPtr mouseHook, keyboardHook;
    static volatile bool watchingInput;
    static int foreignInputVersion, stopLatched;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    // Modifiers include the Windows keys. Button states are physical virtual-key
    // states, so swapped primary/secondary mouse settings do not weaken the guard.
    static readonly int[] HeldInputKeys = { 0x10, 0x11, 0x12, 0x5B, 0x5C,
        0x01, 0x02, 0x04, 0x05, 0x06 };

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X, Y;
        public NativePoint(Point point) { X = point.X; Y = point.Y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size, Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow, ParamHigh;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    // Do not use Pack=1: INPUT's union starts at byte 8 on x64 (byte 4 on
    // x86); pointer-sized dwExtraInfo makes the total size 40 / 28 bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public InputUnion Value;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, [In] Input[] inputs, int inputSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string moduleName);

    // Install only for the duration of the reservation workflow. Low-level hooks
    // run on this message-loop thread; they neither suppress nor inject events.
    public static void BeginInputWatch()
    {
        lock (WatchLock)
        {
            if (watchingInput || mouseHook != IntPtr.Zero || keyboardHook != IntPtr.Zero)
                throw new InvalidOperationException("이전 입력 감시가 아직 종료되지 않았습니다.");
            if (!Application.MessageLoop)
                throw new InvalidOperationException("입력 감시는 창의 UI 메시지 루프에서 시작해야 합니다.");
            Interlocked.Exchange(ref foreignInputVersion, 0);
            Interlocked.Exchange(ref stopLatched, 0);
            IntPtr module = GetModuleHandle(null);
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "입력 감시를 준비하지 못했습니다.");
            try
            {
                mouseHook = SetWindowsHookEx(WhMouseLl, MouseHookCallback, module, 0);
                if (mouseHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "마우스 입력 감시를 시작하지 못했습니다.");
                keyboardHook = SetWindowsHookEx(WhKeyboardLl, KeyboardHookCallback, module, 0);
                if (keyboardHook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "키보드 입력 감시를 시작하지 못했습니다.");
                watchingInput = true;
            }
            catch (Exception installError)
            {
                try { EndInputWatch(); }
                catch (Exception cleanupError)
                {
                    throw new InvalidOperationException("입력 감시 설치와 정리에 실패하여 기능을 중지했습니다.",
                        new AggregateException(installError, cleanupError));
                }
                throw;
            }
        }
    }

    public static void EndInputWatch()
    {
        lock (WatchLock)
        {
            watchingInput = false;
            bool failed = false; int error = 0;
            if (mouseHook != IntPtr.Zero)
            {
                if (UnhookWindowsHookEx(mouseHook)) mouseHook = IntPtr.Zero;
                else { failed = true; error = Marshal.GetLastWin32Error(); }
            }
            if (keyboardHook != IntPtr.Zero)
            {
                if (UnhookWindowsHookEx(keyboardHook)) keyboardHook = IntPtr.Zero;
                else { failed = true; error = Marshal.GetLastWin32Error(); }
            }
            // Keep a failed handle and the static delegates rooted, so another
            // cleanup can be attempted and a new watch cannot silently replace it.
            if (failed) throw new Win32Exception(error, "입력 감시를 종료하지 못해 기능을 중지했습니다.");
        }
    }

    public static uint ForeignInputVersion()
    {
        if (!watchingInput)
            throw new InvalidOperationException("입력 감시가 실행 중이 아닙니다.");
        return unchecked((uint)Interlocked.CompareExchange(ref foreignInputVersion, 0, 0));
    }

    static bool HasOwnMarker(IntPtr data, int extraInfoOffset)
    {
        IntPtr value = Marshal.ReadIntPtr(data, extraInfoOffset);
        return IntPtr.Size == 8
            ? unchecked((ulong)value.ToInt64()) == InputMarker.ToUInt64()
            : unchecked((uint)value.ToInt32()) == InputMarker.ToUInt32();
    }

    static IntPtr ObserveMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && watchingInput)
        {
            try
            {
                // Read only MSLLHOOKSTRUCT.dwExtraInfo: never copy coordinates,
                // button information, timestamps or any event history.
                if (!HasOwnMarker(data, IntPtr.Size == 8 ? 24 : 20))
                    Interlocked.Increment(ref foreignInputVersion);
            }
            catch { Interlocked.Increment(ref foreignInputVersion); Interlocked.Exchange(ref stopLatched, 1); }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    static IntPtr ObserveKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && watchingInput)
        {
            try
            {
                // KBDLLHOOKSTRUCT.dwExtraInfo starts at byte 16 on x86 and x64.
                // Only an F8-down boolean is retained; no key/text log is kept.
                if (!HasOwnMarker(data, 16))
                {
                    Interlocked.Increment(ref foreignInputVersion);
                    long kind = message.ToInt64();
                    if ((kind == WmKeyDown || kind == WmSysKeyDown) && Marshal.ReadInt32(data) == VkF8)
                        Interlocked.Exchange(ref stopLatched, 1);
                }
            }
            catch { Interlocked.Increment(ref foreignInputVersion); Interlocked.Exchange(ref stopLatched, 1); }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    public static bool IsForeground(Form owner)
    {
        return owner != null && !owner.IsDisposed && !owner.Disposing &&
            owner.IsHandleCreated && GetForegroundWindow() == owner.Handle;
    }

    public static bool IsStopPressed()
    {
        return (watchingInput && Interlocked.CompareExchange(ref stopLatched, 0, 0) != 0) || IsHeld(VkF8);
    }

    // GetLastInputInfo uses a wrapping, session-local 32-bit tick count. A caller
    // comparing ticks must use unchecked uint subtraction, not wall-clock time.
    public static uint LastInputTick()
    {
        LastInputInfo info = new LastInputInfo();
        info.Size = (uint)Marshal.SizeOf(typeof(LastInputInfo));
        if (!GetLastInputInfo(ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "마지막 입력 시간을 확인하지 못했습니다.");
        return info.Time;
    }

    public static void Click(Form owner, Point screenPoint)
    {
        ValidatePoint(owner, screenPoint);
        Input[] inputs = { MoveTo(screenPoint), MouseEvent(MouseLeftDown, 0),
            MouseEvent(MouseLeftUp, 0) };
        Send(owner, screenPoint, inputs);
    }

    // delta follows the Windows wheel convention: +120 scrolls one notch up,
    // -120 one notch down. No scroll direction, cadence, or jitter is invented.
    public static void Wheel(Form owner, Point screenPoint, int delta)
    {
        if (delta == 0) throw new ArgumentOutOfRangeException("delta", "스크롤 양은 0일 수 없습니다.");
        ValidatePoint(owner, screenPoint);
        Input[] inputs = { MoveTo(screenPoint), MouseEvent(MouseWheel, unchecked((uint)delta)) };
        Send(owner, screenPoint, inputs);
    }

    public static void PressEnter(Form owner)
    {
        ValidateOwner(owner);
        if (IsHeld(VkEnter))
            throw new InvalidOperationException("Enter 키가 눌려 있어 입력을 중단했습니다.");
        Input[] inputs = { KeyboardEvent(0), KeyboardEvent(KeyUp) };
        Send(owner, null, inputs);
    }

    static bool IsHeld(int key)
    {
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    static void ValidateOwner(Form owner)
    {
        if (owner == null) throw new ArgumentNullException("owner");
        if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated)
            throw new InvalidOperationException("입력 대상 창이 닫혔습니다.");
        if (owner.InvokeRequired)
            throw new InvalidOperationException("마우스와 키보드 입력은 창의 UI 스레드에서 실행해야 합니다.");
        if (!owner.Visible || owner.WindowState == FormWindowState.Minimized || !owner.Enabled || !IsForeground(owner))
            throw new InvalidOperationException("입력 대상 창이 활성 상태가 아니어서 중단했습니다.");
        if (IsStopPressed())
            throw new OperationCanceledException("F8 키로 입력을 중단했습니다.");
        foreach (int key in HeldInputKeys)
            if (IsHeld(key))
                throw new InvalidOperationException("마우스 버튼 또는 보조 키가 눌려 있어 입력을 중단했습니다.");
    }

    static void ValidatePoint(Form owner, Point screenPoint)
    {
        ValidateOwner(owner);
        if (!owner.RectangleToScreen(owner.ClientRectangle).Contains(screenPoint))
            throw new InvalidOperationException("클릭 또는 스크롤 위치가 대상 창의 내용 영역 밖입니다.");
        IntPtr hit = WindowFromPoint(new NativePoint(screenPoint));
        if (hit == IntPtr.Zero || GetAncestor(hit, RootWindow) != owner.Handle)
            throw new InvalidOperationException("입력 위치가 다른 창에 가려져 있어 중단했습니다.");
    }

    static Input MoveTo(Point point)
    {
        int left = GetSystemMetrics(SmXVirtualScreen), top = GetSystemMetrics(SmYVirtualScreen);
        int width = GetSystemMetrics(SmCxVirtualScreen), height = GetSystemMetrics(SmCyVirtualScreen);
        long x = (long)point.X - left, y = (long)point.Y - top;
        if (width <= 1 || height <= 1 || x < 0 || y < 0 || x >= width || y >= height)
            throw new InvalidOperationException("입력 위치가 현재 모니터 영역에 없습니다.");
        Input input = MouseEvent(MouseMove | MouseAbsolute | MouseVirtualDesk, 0);
        // 0..65535 covers the virtual desktop, including negative screen origins.
        input.Value.Mouse.X = (int)((x * 65535L + (width - 1) / 2) / (width - 1));
        input.Value.Mouse.Y = (int)((y * 65535L + (height - 1) / 2) / (height - 1));
        return input;
    }

    static Input MouseEvent(uint flags, uint data)
    {
        Input input = new Input();
        input.Type = InputMouse;
        input.Value.Mouse.Flags = flags;
        input.Value.Mouse.Data = data;
        input.Value.Mouse.ExtraInfo = InputMarker;
        return input;
    }

    static Input KeyboardEvent(uint flags)
    {
        Input input = new Input();
        input.Type = InputKeyboard;
        input.Value.Keyboard.VirtualKey = VkEnter;
        input.Value.Keyboard.Flags = flags;
        input.Value.Keyboard.ExtraInfo = InputMarker;
        return input;
    }

    static void Send(Form owner, Point? point, Input[] inputs)
    {
        int size = Marshal.SizeOf(typeof(Input));
        if (size != (IntPtr.Size == 8 ? 40 : 28))
            throw new InvalidOperationException("Windows 입력 구조체 크기가 올바르지 않습니다.");
        // Recheck immediately before injection. These checks cannot make focus
        // changes or layout changes atomic with Windows' input dispatch. Callers
        // must stop on failure and verify the resulting page before continuing.
        if (point.HasValue) ValidatePoint(owner, point.Value);
        else
        {
            ValidateOwner(owner);
            if (IsHeld(VkEnter))
                throw new InvalidOperationException("Enter 키가 눌려 있어 입력을 중단했습니다.");
        }
        uint inserted = SendInput((uint)inputs.Length, inputs, size);
        if (inserted != (uint)inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            // SendInput may be blocked by UIPI without reporting a useful error.
            // Never elevate, retry, or inject cleanup keys into an unknown state.
            throw new InvalidOperationException(String.Format(
                "Windows 입력 전달이 완료되지 않았습니다 ({0}/{1}, 오류 {2}). 동작을 중단했습니다. 마우스와 키 상태를 확인하세요.",
                inserted, inputs.Length, error));
        }
    }
}
