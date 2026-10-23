using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    static extern IntPtr BackgroundTestForegroundWindow();

    // Integration checks for the actual embedded WebView protocol transport.
    // The fixture is an isolated local document. The host test briefly observes
    // input with the normal hooks, but sends no native input, activates no window,
    // and makes no Korail request, reservation or Telegram call.
    async Task RunBackgroundInputTests()
    {
        if (!Program.SelfTest || mouseBookingActive || queueRunning)
            throw new InvalidOperationException("중지된 로컬 모의 검사에서만 페이지 입력을 검사할 수 있습니다.");
        int noticesBefore = demoNotifications.Count;
        Assert(backgroundInputCheck != null && backgroundInputCheck.Checked && BackgroundInputEnabled,
            "background input: new session defaults to page-only background input with the control checked");
        Assert(backgroundInputCheck.Enabled && backgroundInputCheck.Visible && backgroundInputCheck.TabStop &&
            backgroundInputCheck.AccessibleName == "다른 앱과 동시 작업",
            "background input: native-input opt-out is available and accessible in the main window");
        bool manualDiagnostic = Program.ManualDiagnostic;
        try
        {
            Program.ManualDiagnostic = true;
            Assert(!BackgroundInputEnabled,
                "background input: manual diagnostic policy disables page-only automation even when the stored mode is enabled");
        }
        finally { Program.ManualDiagnostic = manualDiagnostic; }
        Exception failure = null;
        try
        {
            await LoadCardDemo("available");
            ValidateBackgroundTestDocument();
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              window.KorailDesktopHelper.stop('local background input test');
              document.documentElement.style.cssText='height:100%;overflow:hidden';
              document.body.style.cssText='margin:0;height:100%;overflow:hidden';
              document.body.innerHTML='<div id=background-fixture style=position:fixed;inset:0;background:white;z-index:2147483647><button id=background-click style=position:absolute;left:16px;top:12px;width:180px;height:38px>Local input test</button><input id=background-station name=searchTxt title=station style=position:absolute;left:16px;top:62px;width:220px;height:32px><div id=background-scroll style=position:absolute;left:16px;top:112px;bottom:12px;right:16px;overflow:auto><div style=height:3000px>Local scrolling fixture</div></div></div>';
              const s=window.__backgroundInputSelfTest={clicks:0,inputs:0,wheels:0,clickTrusted:false,inputTrusted:false,wheelTrusted:false};
              const station=document.getElementById('background-station');station.value='기존역';
              document.getElementById('background-click').addEventListener('click',e=>{s.clicks++;s.clickTrusted=e.isTrusted;});
              station.addEventListener('input',e=>{s.inputs++;s.inputTrusted=e.isTrusted;});
              document.getElementById('background-scroll').addEventListener('wheel',e=>{s.wheels++;s.wheelTrusted=e.isTrusted;},{passive:true});
            })()");
            int page = navigation;
            string source = view.CoreWebView2.Source;
            Action validate = delegate
            {
                ValidateBackgroundTestDocument();
                if (page != navigation || source != view.CoreWebView2.Source || navigationPending)
                    throw new OperationCanceledException("로컬 검사 문서가 바뀌었습니다.");
            };
            int commands = 0;
            Func<string, string, Task<string>> dispatch = async (method, payload) =>
            {
                validate(); commands++;
                return await view.CoreWebView2.CallDevToolsProtocolMethodAsync(method, payload);
            };
            var input = new PageInputDispatcher(dispatch, validate);
            Point pointer = Cursor.Position;
            IntPtr foreground = BackgroundTestForegroundWindow();

            var button = await BackgroundTestPoint("#background-click");
            await input.ClickAsync(NumberValue(button, "x"), NumberValue(button, "y"), NumberValue(button, "width"), NumberValue(button, "height"));
            Assert(await EvalBool("__backgroundInputSelfTest.clicks===1 && __backgroundInputSelfTest.clickTrusted"),
                "background input: real WebView click activates one local button");
            Assert(Cursor.Position == pointer && BackgroundTestForegroundWindow() == foreground,
                "background input: local button click leaves the OS pointer and foreground window unchanged");

            var station = await BackgroundTestPoint("#background-station");
            await input.ClickAsync(NumberValue(station, "x"), NumberValue(station, "y"), NumberValue(station, "width"), NumberValue(station, "height"));
            Assert(await EvalBool("document.activeElement===document.getElementById('background-station')"),
                "background input: page click focuses the local station field");
            await input.ReplaceStationTextAsync("전주");
            Assert(await EvalBool("document.getElementById('background-station').value==='전주' && __backgroundInputSelfTest.inputs===1 && __backgroundInputSelfTest.inputTrusted"),
                "background input: actual page Ctrl+A and Unicode insertion replace station text instead of appending");
            Assert(Cursor.Position == pointer && BackgroundTestForegroundWindow() == foreground,
                "background input: station replacement leaves the OS pointer and foreground window unchanged");

            var scroll = await BackgroundTestPoint("#background-scroll");
            await input.WheelAsync(NumberValue(scroll, "x"), NumberValue(scroll, "y"), NumberValue(scroll, "width"), NumberValue(scroll, "height"), -360);
            DateTime scrollDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < scrollDeadline && !await EvalBool("document.getElementById('background-scroll').scrollTop>0"))
                await Task.Delay(50);
            Assert(await EvalBool("document.getElementById('background-scroll').scrollTop>0 && __backgroundInputSelfTest.wheels===1 && __backgroundInputSelfTest.wheelTrusted"),
                "background input: actual page wheel scrolls only the local scroll container");
            Assert(Cursor.Position == pointer && BackgroundTestForegroundWindow() == foreground,
                "background input: wheel leaves the OS pointer and foreground window unchanged");

            int beforeInvalid = commands;
            await RejectBackgroundInput<ArgumentOutOfRangeException>(() => input.ClickAsync(-1, 10, NumberValue(button, "width"), NumberValue(button, "height")),
                "background input: outside viewport point is rejected before transport");
            await RejectBackgroundInput<ArgumentOutOfRangeException>(() => input.WheelAsync(10, 10, NumberValue(button, "width"), NumberValue(button, "height"), 0),
                "background input: zero wheel delta is rejected before transport");
            Assert(commands == beforeInvalid && await EvalBool("__backgroundInputSelfTest.clicks===1"),
                "background input: invalid coordinates and wheel delta send no page command");

            var blocked = new PageInputDispatcher(dispatch, delegate { throw new OperationCanceledException("local stopped guard"); });
            await RejectBackgroundInput<OperationCanceledException>(() => blocked.ClickAsync(20, 20, NumberValue(button, "width"), NumberValue(button, "height")),
                "background input: stopped guard prevents the first command");
            Assert(commands == beforeInvalid, "background input: stopped guard sends no command or fallback");

            // Both key commands are allowed; the guard then simulates a stopped
            // run before text insertion. No synthetic key remains held afterward.
            int validations = 0;
            var stoppedText = new PageInputDispatcher(dispatch, delegate
            {
                validate();
                if (++validations == 3) throw new OperationCanceledException("local stopped before insertion");
            });
            await RejectBackgroundInput<OperationCanceledException>(() => stoppedText.ReplaceStationTextAsync("서울"),
                "background input: a guard change between awaited commands blocks text insertion");
            Assert(commands == beforeInvalid + 2 && await EvalBool("document.getElementById('background-station').value==='전주' && __backgroundInputSelfTest.inputs===1"),
                "background input: stopped text replacement sends no insertion, retry or native fallback");
            Assert(Cursor.Position == pointer && BackgroundTestForegroundWindow() == foreground,
                "background input: rejected and interrupted inputs leave OS interaction unchanged");
            await RunBackgroundHostInputTests(button);
            Assert(demoNotifications.Count == noticesBefore && !mouseBookingActive && !queueRunning && !sleepPrevention.Active,
                "background input: fixture sends no reservation notice and starts no automation loop");
        }
        catch (Exception error) { failure = error; }
        // C# 5 cannot await in finally. Restore before rethrowing the original
        // error so subsequent tests cannot consume this altered local fixture.
        if (!closing && !IsDisposed) await LoadCardDemo("available");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    async Task RunBackgroundHostInputTests(Dictionary<string, object> button)
    {
        ValidateBackgroundTestDocument();
        bool priorDemo = mouseDemo;
        string priorPhase = mousePhase;
        bool watching = false;
        try
        {
            // Do not change focus to arrange the fixture. Earlier preview dialogs
            // may have activated the transparent owner. Exercise the real host
            // path either way; when it starts inactive it must remain inactive.
            bool beganForeground = NativeMouseInput.IsForeground(this);
            Assert(InputWindowReady(), "background host: page-only input is ready without imposing a foreground prerequisite");
            mouseDemo = true; mousePhase = "select"; mouseBookingActive = true;
            mouseGeneration++;
            BeginAutomationInputWatch(); watching = true;
            mouseLastInput = NativeMouseInput.ForeignInputVersion();
            int run = mouseGeneration;
            for (int i = 0; i < 3; i++)
            {
                await PreparePointerAutomation(NumberValue(button, "x"), NumberValue(button, "y"), NumberValue(button, "width"), NumberValue(button, "height"), run);
                await ClickAutomation(NumberValue(button, "x"), NumberValue(button, "y"), NumberValue(button, "width"), NumberValue(button, "height"), run);
                await Task.Delay(80);
                Assert(mouseBookingActive && !mousePaused && run == mouseGeneration,
                    "background host: repeated page input keeps the run active " + (i + 1));
            }
            Assert(await EvalBool("__backgroundInputSelfTest.clicks===4"), "background host: three host clicks reach the local button");
            await WheelAutomation(-120, run);
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && !await EvalBool("__backgroundInputSelfTest.wheels===2")) await Task.Delay(50);
            Assert(await EvalBool("__backgroundInputSelfTest.wheels===2"), "background host: host scroll reaches the local page");
            Assert(beganForeground || !NativeMouseInput.IsForeground(this),
                "background host: input does not acquire foreground when the run starts inactive");
            mouseGeneration++;
            await RejectBackgroundInput<OperationCanceledException>(() => ClickAutomation(NumberValue(button, "x"), NumberValue(button, "y"), NumberValue(button, "width"), NumberValue(button, "height"), run),
                "background host: invalidated run still rejects another click");
            Assert(await EvalBool("__backgroundInputSelfTest.clicks===4"), "background host: stop prevents another page click");
        }
        finally
        {
            mouseBookingActive = false; mouseGeneration++;
            mouseDemo = priorDemo; mousePhase = priorPhase;
            if (watching) NativeMouseInput.EndInputWatch();
        }
    }

    void ValidateBackgroundTestDocument()
    {
        Uri source;
        if (!Program.SelfTest || !ready || closing || view.CoreWebView2 == null ||
            !Uri.TryCreate(view.CoreWebView2.Source, UriKind.Absolute, out source) ||
            source.Scheme != "https" || source.Host != "korail-helper.invalid" || source.AbsolutePath != "/card-demo.html")
            throw new InvalidOperationException("페이지 입력 통합 검사는 로컬 카드 모의 화면 전용입니다.");
    }

    async Task<Dictionary<string, object>> BackgroundTestPoint(string selector)
    {
        ValidateBackgroundTestDocument();
        string point = await view.CoreWebView2.ExecuteScriptAsync("(() => {const r=document.querySelector(" + json.Serialize(selector) + ").getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2,width:innerWidth,height:innerHeight};})()");
        return json.Deserialize<Dictionary<string, object>>(point);
    }

    async Task RejectBackgroundInput<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Assert(true, name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
}
