using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Sends input only to the supplied WebView protocol endpoint. It never moves
// the Windows pointer, activates a window, retries, or falls back to SendInput.
internal sealed class PageInputDispatcher
{
    readonly Func<string, string, Task<string>> dispatch;
    readonly Action validate;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    int inFlight;

    public PageInputDispatcher(Func<string, string, Task<string>> dispatch, Action validate)
    {
        if (dispatch == null) throw new ArgumentNullException("dispatch");
        if (validate == null) throw new ArgumentNullException("validate");
        this.dispatch = dispatch; this.validate = validate;
    }

    public async Task ClickAsync(double x, double y, double viewportWidth, double viewportHeight)
    {
        ValidatePoint(x, y, viewportWidth, viewportHeight);
        Begin();
        try
        {
            // CDP uses CSS viewport coordinates, not screen/device pixels.
            await SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x = x, y = y, button = "left", buttons = 1, clickCount = 1, modifiers = 0 });
            await SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x = x, y = y, button = "left", buttons = 0, clickCount = 1, modifiers = 0 });
        }
        finally { End(); }
    }

    public async Task WheelAsync(double x, double y, double viewportWidth, double viewportHeight, int windowsDelta)
    {
        ValidatePoint(x, y, viewportWidth, viewportHeight);
        if (windowsDelta == 0 || windowsDelta < -1200 || windowsDelta > 1200)
            throw new ArgumentOutOfRangeException("windowsDelta", "스크롤 양을 확인하세요.");
        Begin();
        try
        {
            // Windows wheel positive means up; CDP deltaY positive means down.
            await SendAsync("Input.dispatchMouseEvent", new { type = "mouseWheel", x = x, y = y, deltaX = 0, deltaY = -windowsDelta, button = "none", buttons = 0, modifiers = 0 });
        }
        finally { End(); }
    }

    public async Task ReplaceStationTextAsync(string text)
    {
        if (String.IsNullOrWhiteSpace(text) || text.Length > 40 || Array.Exists(text.ToCharArray(), Char.IsControl))
            throw new ArgumentException("역 이름을 확인하세요.", "text");
        Begin();
        try
        {
            // The host must have just verified the station input's DOM focus.
            // Each event declares its own modifiers; no OS key is pressed.
            await SendAsync("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 });
            await SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "a", code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 0 });
            await SendAsync("Input.insertText", new { text = text });
        }
        finally { End(); }
    }

    async Task SendAsync(string method, object parameters)
    {
        string payload = json.Serialize(parameters);
        // A navigation, stop, blocked page, or changed input state between two
        // awaited commands must prevent the next command from being submitted.
        validate();
        Task<string> operation = dispatch(method, payload);
        if (operation == null) throw new InvalidOperationException("페이지 입력 작업을 확인하지 못했습니다.");
        await operation;
    }

    void Begin()
    {
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
            throw new InvalidOperationException("다른 페이지 입력이 끝나지 않았습니다.");
    }

    void End() { Interlocked.Exchange(ref inFlight, 0); }

    static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    static void ValidatePoint(double x, double y, double width, double height)
    {
        if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0 || width > 32768 || height > 32768)
            throw new ArgumentOutOfRangeException("viewport", "브라우저 크기를 확인하지 못했습니다.");
        if (!Finite(x) || !Finite(y) || x < 0 || y < 0 || x >= width || y >= height)
            throw new ArgumentOutOfRangeException("point", "입력 위치가 브라우저 화면 밖입니다.");
    }
}
