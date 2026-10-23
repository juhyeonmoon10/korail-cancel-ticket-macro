using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

internal sealed partial class MainWindow
{
    bool backgroundInput, automationInputBusy;
    CheckBox backgroundInputCheck;
    Button pageNoticeButton;
    CoreWebView2ScriptDialogOpeningEventArgs pendingPageNotice;
    CoreWebView2Deferral pendingPageNoticeDeferral;

    bool BackgroundInputEnabled { get { return backgroundInput && !Program.ManualDiagnostic; } }

    bool InputWindowReady()
    {
        return !closing && !IsDisposed && Visible && Enabled && WindowState != FormWindowState.Minimized &&
            view != null && !view.IsDisposed && view.Visible &&
            (BackgroundInputEnabled || NativeMouseInput.IsForeground(this));
    }

    void BeginAutomationInputWatch() { NativeMouseInput.BeginInputWatch(this, BackgroundInputEnabled); }

    async void ChangeBackgroundInput(object sender, EventArgs e)
    {
        // Changing transport always invalidates an in-flight run before the new
        // mode is enabled. A failed page input never falls back to global input.
        StopBookingQueue("입력 방식을 변경해 중지했습니다.");
        StopMouseBooking(null);
        backgroundInput = backgroundInputCheck.Checked;
        if (ready) await StopHelper(BackgroundInputEnabled
            ? "동시 작업 모드 · 다른 앱을 사용할 수 있습니다. 도우미는 최소화하지 마세요. 실제 코레일 호환성은 미검증입니다."
            : "마우스 모드 · 도우미를 앞에 두고 실행하세요.");
        UpdateShellState();
    }

    Action InputValidation(int run, bool queued, bool mode, int page, string source, Size viewport, uint input)
    {
        return delegate
        {
            if (mode != BackgroundInputEnabled || !InputWindowReady() || !AutomationAvailable() || pendingPageNotice != null)
                throw new InvalidOperationException("입력 대상 또는 실행 방식이 바뀌어 중지했습니다.");
            if (queued)
            {
                if (!QueueQueryCurrent(run)) throw new OperationCanceledException("예약 순서가 중지됐습니다.");
                CheckQueueInput();
            }
            else if (run != mouseGeneration || !mouseBookingActive || mousePaused)
                throw new OperationCanceledException("예약 입력이 중지됐습니다.");
            if (NativeMouseInput.IsStopPressed()) throw new OperationCanceledException("F8로 중지했습니다.");
            if (NativeMouseInput.ForeignInputVersion() != input)
                throw new OperationCanceledException("도우미 직접 입력이 감지돼 중지했습니다.");
            if (MouseDocumentChanged(page, source) || viewport != view.ClientSize ||
                !(queued ? OfficialTicketSource(source) : MouseAllowedSource(source)))
                throw new InvalidOperationException("입력 직전 페이지나 브라우저 크기가 바뀌었습니다.");
        };
    }

    async Task ClickAutomation(double x, double y, double width, double height, int run, bool queued = false)
    {
        if (automationInputBusy) throw new InvalidOperationException("이전 입력이 완료될 때까지 다시 시작할 수 없습니다.");
        automationInputBusy = true;
        try
        {
        bool mode = BackgroundInputEnabled;
        var validate = InputValidation(run, queued, mode, navigation, view.CoreWebView2.Source, view.ClientSize,
            queued ? queueInputVersion : mouseLastInput);
        validate();
        if (mode)
        {
            var input = new PageInputDispatcher(view.CoreWebView2.CallDevToolsProtocolMethodAsync, validate);
            await input.ClickAsync(x, y, width, height);
        }
        else NativeMouseInput.Click(this, view.PointToScreen(new Point((int)Math.Round(x * view.ClientSize.Width / width), (int)Math.Round(y * view.ClientSize.Height / height))));
        }
        finally { automationInputBusy = false; }
    }

    async Task WheelAutomation(int delta, int run, bool queued = false)
    {
        if (automationInputBusy) throw new InvalidOperationException("이전 입력이 완료될 때까지 다시 시작할 수 없습니다.");
        automationInputBusy = true;
        try
        {
        bool mode = BackgroundInputEnabled;
        var validate = InputValidation(run, queued, mode, navigation, view.CoreWebView2.Source, view.ClientSize,
            queued ? queueInputVersion : mouseLastInput);
        validate();
        if (mode)
        {
            var dimensions = json.Deserialize<Dictionary<string, object>>(await view.CoreWebView2.ExecuteScriptAsync("({width:innerWidth,height:innerHeight,hidden:document.hidden})"));
            validate();
            if (dimensions == null || !dimensions.ContainsKey("hidden") || !Object.Equals(dimensions["hidden"], false))
                throw new InvalidOperationException("브라우저 화면이 숨겨져 스크롤을 중지했습니다.");
            double width = NumberValue(dimensions, "width"), height = NumberValue(dimensions, "height");
            var input = new PageInputDispatcher(view.CoreWebView2.CallDevToolsProtocolMethodAsync, validate);
            await input.WheelAsync(width / 2, height / 2, width, height, delta);
        }
        else NativeMouseInput.Wheel(this, view.PointToScreen(new Point(view.ClientSize.Width / 2, view.ClientSize.Height / 2)), delta);
        }
        finally { automationInputBusy = false; }
    }

    async Task TypeStationAutomation(string text, int run)
    {
        if (automationInputBusy) throw new InvalidOperationException("이전 입력이 완료될 때까지 다시 시작할 수 없습니다.");
        automationInputBusy = true;
        try
        {
        bool mode = BackgroundInputEnabled;
        var validate = InputValidation(run, true, mode, navigation, view.CoreWebView2.Source, view.ClientSize, queueInputVersion);
        validate();
        if (mode)
        {
            var input = new PageInputDispatcher(async delegate(string method, string parameters)
            {
                bool focused = await EvalBool(@"!document.hidden && (() => {const e=document.activeElement;return e && e.matches('input[name=searchTxt][title=""역명을 입력해주세요""]') && !e.disabled && !e.readOnly && e.closest('.layerWrap.type_tranin-station-pop_wrap') && e.getClientRects().length>0;})()");
                validate();
                if (!focused) throw new InvalidOperationException("역 검색 입력창의 포커스가 바뀌어 입력을 중지했습니다.");
                return await view.CoreWebView2.CallDevToolsProtocolMethodAsync(method, parameters);
            }, validate);
            await input.ReplaceStationTextAsync(text);
        }
        else NativeMouseInput.ReplaceStationText(this, text);
        }
        finally { automationInputBusy = false; }
    }

    void DeferPageNotice(CoreWebView2ScriptDialogOpeningEventArgs e, CoreWebView2Deferral deferral)
    {
        ClearDeferredPageNotice();
        pendingPageNotice = e; pendingPageNoticeDeferral = deferral;
        pageNoticeButton.Visible = true;
        status.Text = "페이지 안내로 중지했습니다. 도우미 상단 ‘안내 확인’을 눌러 내용을 확인하세요.";
    }

    void ClearDeferredPageNotice()
    {
        var deferral = pendingPageNoticeDeferral;
        pendingPageNotice = null; pendingPageNoticeDeferral = null;
        if (pageNoticeButton != null) pageNoticeButton.Visible = false;
        if (deferral != null) deferral.Complete();
    }

    void OpenDeferredPageNotice()
    {
        var notice = pendingPageNotice; var deferral = pendingPageNoticeDeferral;
        if (notice == null || deferral == null) return;
        pendingPageNotice = null; pendingPageNoticeDeferral = null; pageNoticeButton.Visible = false;
        ShowPageScriptDialog(notice, deferral, false, mouseGeneration);
    }
}
