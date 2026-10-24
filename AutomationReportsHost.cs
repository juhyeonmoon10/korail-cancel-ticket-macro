using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    sealed class AutomationReportContext
    {
        public AutomationReportSession Policy;
        public TelegramSettings Destination;
        public string Mode, LastTarget;
        public BookingTarget TargetBeforePreparation;
        public long Queries;
        public bool Started, Ended, PreserveFinalProgress, SuppressNetwork;
        public readonly List<string> JobIds = new List<string>();
    }

    sealed class PendingAutomationReport
    {
        public AutomationReportContext Context;
        public string Kind, Text;
    }

    readonly Label automationReportStatus = new Label { AccessibleName = "자동 예매 진행 보고 전송 상태", AutoEllipsis = true };
    readonly List<PendingAutomationReport> automationReportOutbox = new List<PendingAutomationReport>();
    AutomationReportContext automationReportContext;
    bool automationReportSending;
    Task automationReportDrain = Task.FromResult(0);
    Func<TelegramSettings, string, Task<string>> automationReportTestSink;
    const int MaximumPendingAutomationReports = 16;

    void PrepareAutomationReports(string mode)
    {
        StopAutomationReports();
        automationReportOutbox.RemoveAll(item => item.Kind != "error");
        var destination = settings.Telegram.Copy();
        var context = new AutomationReportContext {
            Destination = destination, Policy = new AutomationReportSession(destination), Mode = mode,
            TargetBeforePreparation = mouseTarget,
            SuppressNetwork = Program.SelfTest || Program.Demo || Program.ManualDiagnostic || AutomationReportLocalPage()
        };
        if (!String.IsNullOrEmpty(mode) && mode.Contains("순서") && queueStore != null)
        {
            try { context.JobIds.AddRange(queueStore.GetAll().Where(job => job.State == BookingQueueState.Pending || job.State == BookingQueueState.Running).Select(job => job.Id)); }
            catch { /* Reporting must not change booking-store error handling. */ }
        }
        automationReportContext = context;
        if (!closing && !automationReportStatus.IsDisposed)
            automationReportStatus.Text = destination.Enabled ? "자동 예매 보고 준비됨" : "자동 예매 보고 꺼짐";
    }

    bool AutomationReportLocalPage()
    {
        try { return ready && view.CoreWebView2 != null && view.CoreWebView2.Source.StartsWith(LocalBase, StringComparison.Ordinal); }
        catch { return false; }
    }

    void AutomationReportsStarted()
    {
        var context = automationReportContext;
        if (context == null || context.Ended) return;
        context.Started = true;
        EnqueueAutomationReport(context, "start", context.Policy.TryStart(AutomationReportSnapshotFor(context)));
    }

    void RecordAutomationQueryAttempt()
    {
        var context = automationReportContext;
        if (context != null && !context.Ended && context.Queries < Int64.MaxValue) context.Queries++;
    }

    void ReportAutomationProgress()
    {
        var context = automationReportContext;
        if (context == null || context.Ended) return;
        EnqueueAutomationReport(context, "progress", context.Policy.TryProgress(AutomationReportSnapshotFor(context)));
    }

    void ReportAutomationFailure(string reason, Exception exception = null, string code = null, string requiredAction = null)
    {
        var context = automationReportContext;
        if (context == null || context.Ended || (String.IsNullOrEmpty(code) && NormalAutomationInterruption(reason, exception))) return;
        var snapshot = AutomationReportSnapshotFor(context);
        var error = AutomationReportFailure(reason, exception, code, requiredAction, mouseSubmitted);
        string message = context.Policy.TryError(snapshot, error);
        automationReportOutbox.RemoveAll(item => item.Context == context && item.Kind != "error");
        EnqueueAutomationReport(context, "error", message);
    }

    void StopAutomationReports(bool preserveFinalProgress = false)
    {
        var context = automationReportContext;
        if (context == null) return;
        context.Ended = true;
        context.PreserveFinalProgress = preserveFinalProgress;
        context.Policy.Stop();
        automationReportOutbox.RemoveAll(item => item.Context == context && item.Kind != "error" &&
            !(preserveFinalProgress && item.Kind == "progress"));
    }

    AutomationReportSnapshot AutomationReportSnapshotFor(AutomationReportContext context)
    {
        var targets = queueRunning && queueJob != null ? queueJob.Targets : mouseTargets;
        if ((targets == null || targets.Count == 0) && mouseTarget != null &&
            (mouseBookingActive || mouseSubmitted || !Object.ReferenceEquals(mouseTarget, context.TargetBeforePreparation)))
            targets = new List<BookingTarget> { mouseTarget };
        if (targets != null && targets.Count > 0)
        {
            var first = targets[0];
            context.LastTarget = first.Date + " · " + first.DepartureStation + " → " + first.ArrivalStation + " · " +
                String.Join(", ", targets.Take(6).Select(target => target.Train + " " + target.Departure + " " + target.Grade)) +
                (targets.Count > 6 ? " 외 " + (targets.Count - 6).ToString(CultureInfo.InvariantCulture) + "편" : "") +
                " · " + first.Passengers.ToString(CultureInfo.InvariantCulture) + "명";
        }
        int index = 0, total = 0;
        if (queueRunning && queueJob != null)
        {
            if (!context.JobIds.Contains(queueJob.Id)) context.JobIds.Add(queueJob.Id);
            index = context.JobIds.IndexOf(queueJob.Id) + 1; total = context.JobIds.Count;
        }
        string stage = mousePaused ? "일시정지" : queueRunning && queueStage == "query" ? "다음 날짜·구간 조회" :
            queueRunning && queueStage == "notifying" ? "예약 완료 알림 처리" : mouseSubmitted ? "예매 요청 후 공식 결과 확인" :
            mouseRefreshPending ? "추가 조회 결과 확인" : mouseReadAll || mouseMorePending ? "더보기 목록 확인" :
            mouseBookingActive && mousePhase != "select" ? "좌석·예매 안내 처리" : mouseBookingActive ? (mouseRefreshOnly ? "자동 조회" : "후보 열차 좌석 확인") :
            context.Started ? "자동 예매 중지 처리" : (String.IsNullOrEmpty(context.Mode) ? "시작 준비" : context.Mode + " 준비");
        string currentStatus = status == null || status.IsDisposed ? "상태 확인 중" : status.Text;
        if (queueRunning && queueJob != null)
            currentStatus += " · 현재 예약 작업의 누적 추가 조회 시도 " + queueJob.RefreshCount.ToString(CultureInfo.InvariantCulture) + "회";
        return new AutomationReportSnapshot { QueryCount = context.Queries, JobIndex = index, JobCount = total,
            TargetSummary = context.LastTarget, Stage = stage, Status = currentStatus };
    }

    static bool NormalAutomationInterruption(string reason, Exception exception)
    {
        if (exception is OperationCanceledException) return true;
        return KnownNormalAutomationMessage(reason) || (exception != null && KnownNormalAutomationMessage(exception.Message));
    }

    static bool KnownNormalAutomationMessage(string reason)
    {
        string value = reason ?? "";
        return value.StartsWith("F8로 중지", StringComparison.Ordinal) || value == "사용자가 모두 중지했습니다." ||
            value.StartsWith("직접 입력이 감지돼", StringComparison.Ordinal) || value.StartsWith("도우미 직접 입력이 감지돼", StringComparison.Ordinal) ||
            value.StartsWith("사용자 입력으로 자동 진행을 중지", StringComparison.Ordinal) ||
            value.StartsWith("창이 바뀌어 조회 조건 입력을 중지", StringComparison.Ordinal);
    }

    static AutomationReportError AutomationReportFailure(string reason, Exception exception, string code, string requiredAction, bool submitted)
    {
        string observed = String.IsNullOrWhiteSpace(reason) ? "상세 원인을 확인하지 못했습니다." : reason;
        if (exception is InvalidOperationException || exception is ArgumentException || exception is TimeoutException)
        {
            if (!String.IsNullOrWhiteSpace(exception.Message) && !observed.Contains(exception.Message)) observed += " " + exception.Message;
        }
        else if (exception != null)
        {
            // Runtime exception messages can contain URLs, paths or native dumps.
            observed += " (" + exception.GetType().Name + ", HRESULT 0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")";
        }
        string category = "진행 상태 확인 실패";
        string action = "도우미의 현재 화면과 오류 안내를 확인하세요. 로그인·조회 조건·선택 열차가 맞고 진행 중인 예약이 없는지 확인한 뒤 다시 시작하세요.";
        if (code == "browser-process" || exception is COMException)
        {
            code = String.IsNullOrEmpty(code) ? "browser-runtime" : code; category = "브라우저 실행 오류";
            action = "도우미를 종료한 뒤 다시 실행하세요. 코레일 로그인과 날짜·구간·선택 열차를 확인한 뒤 자동 예매를 다시 시작하세요. 반복되면 오류 내용과 브라우저 진단을 확인하세요.";
        }
        else if (code == "input-cleanup")
        {
            category = "입력 감시 정리 실패";
            action = "도우미를 완전히 종료한 뒤 다시 실행하세요. 재실행 전 공식 예약 내역을 확인하고, 로그인과 열차 조건을 다시 확인하세요.";
        }
        else if (code == "site-block" || observed.Contains("자동화 차단") || observed.Contains("차단되었습니다"))
        {
            code = "site-block"; category = "공식 사이트 차단 확인";
            action = "자동 요청을 계속하지 마세요. 코레일 공식 화면의 안내를 직접 확인하세요. 도우미의 ‘브라우저 진단 저장’으로 확인된 차단 기록을 저장할 수 있습니다.";
        }
        else if (submitted)
        {
            code = String.IsNullOrEmpty(code) ? "reservation-result-unknown" : code; category = "예매 요청 후 결과 미확인";
            action = "예매 버튼을 다시 누르지 말고 코레일 공식 예약 내역을 먼저 확인하세요. 해당 예약 상세를 연 뒤 도우미의 ‘예약 기록 → 현재 화면에서 완료 확인’을 이용하세요.";
        }
        else if (observed.Contains("안내") || observed.Contains("로그인") || observed.Contains("인증"))
        {
            code = String.IsNullOrEmpty(code) ? "manual-attention" : code; category = "공식 화면 직접 확인 필요";
            action = "도우미 상단의 ‘안내 확인’이나 코레일 화면에서 안내·로그인·인증 상태를 직접 확인하고, 선택한 열차 조건을 확인한 뒤 다시 시작하세요.";
        }
        else if (observed.Contains("30초") || exception is TimeoutException)
        {
            code = String.IsNullOrEmpty(code) ? "step-timeout" : code; category = "단계 확인 시간 초과";
            action = "인터넷 연결과 코레일 화면의 로딩·오류 안내를 확인하세요. 현재 조회 또는 예약 결과를 확인한 뒤에만 다시 시작하세요.";
        }
        return new AutomationReportError { Code = String.IsNullOrEmpty(code) ? "automation-step" : code, Category = category,
            ObservedReason = observed, RequiredAction = String.IsNullOrWhiteSpace(requiredAction) ? action : requiredAction, ReservationResultUnknown = submitted };
    }

    void EnqueueAutomationReport(AutomationReportContext context, string kind, string message)
    {
        if (String.IsNullOrEmpty(message)) return;
        if (kind == "progress") automationReportOutbox.RemoveAll(item => item.Context == context && item.Kind == "progress");
        if (automationReportOutbox.Count >= MaximumPendingAutomationReports)
        {
            int disposable = automationReportOutbox.FindIndex(item => item.Kind != "error");
            if (disposable < 0) disposable = 0;
            automationReportOutbox.RemoveAt(disposable);
            if (!closing && !automationReportStatus.IsDisposed) automationReportStatus.Text = "보고 대기열이 가득 차 오래된 미전송 보고 1건을 제외했습니다. 현재 오류는 앱에서도 확인하세요.";
        }
        var pending = new PendingAutomationReport { Context = context, Kind = kind, Text = message };
        if (kind == "error") automationReportOutbox.Insert(0, pending); else automationReportOutbox.Add(pending);
        if (!automationReportSending) automationReportDrain = DrainAutomationReportsAsync();
    }

    async Task DrainAutomationReportsAsync()
    {
        automationReportSending = true;
        try
        {
            while (automationReportOutbox.Count > 0)
            {
                var pending = automationReportOutbox[0]; automationReportOutbox.RemoveAt(0);
                var context = pending.Context;
                if (pending.Kind != "error" && (context != automationReportContext || context.Ended && !(context.PreserveFinalProgress && pending.Kind == "progress"))) continue;
                string outcome;
                try
                {
                    if (Program.SelfTest && automationReportTestSink != null)
                        outcome = await automationReportTestSink(context.Destination.Copy(), pending.Text);
                    else if (context.SuppressNetwork || Program.SelfTest || Program.Demo || Program.ManualDiagnostic)
                        outcome = "모의·진단 실행에서는 실제 텔레그램 보고를 보내지 않습니다.";
                    else outcome = await TelegramNotifier.SendAsync(context.Destination, pending.Text);
                }
                catch { outcome = "보고 전송 중 오류가 발생했습니다. 텔레그램 대화와 인터넷 연결을 확인하세요. 자동 재전송하지 않았습니다."; }
                if (!closing && !automationReportStatus.IsDisposed &&
                    (pending.Kind == "error" || context == automationReportContext && (!context.Ended || context.PreserveFinalProgress)))
                    automationReportStatus.Text = outcome == TelegramNotifier.SuccessMessage ?
                        "텔레그램 " + (pending.Kind == "error" ? "오류" : pending.Kind == "start" ? "시작" : "중간") + " 보고 전송 완료" : "자동 예매 보고: " + outcome;
            }
        }
        catch
        {
            if (!closing && !automationReportStatus.IsDisposed) automationReportStatus.Text = "자동 예매 보고 처리에 실패했습니다. 앱의 현재 상태와 텔레그램 대화를 확인하세요.";
        }
        finally { automationReportSending = false; }
    }

    void SetAutomationReportTestSink(Func<TelegramSettings, string, Task<string>> sink)
    {
        if (!Program.SelfTest) throw new InvalidOperationException("보고 전송 대체는 로컬 모의 검사에서만 사용할 수 있습니다.");
        automationReportTestSink = sink;
    }
}
