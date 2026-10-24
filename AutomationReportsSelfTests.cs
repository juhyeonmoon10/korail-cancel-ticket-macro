using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

internal sealed partial class MainWindow
{
    async Task RunAutomationReportHostTests()
    {
        if (!Program.SelfTest || mouseBookingActive || queueRunning || automationReportSending)
            throw new InvalidOperationException("중지된 로컬 모의 검사에서만 보고 전송을 검사할 수 있습니다.");
        var originalTelegram = settings.Telegram;
        var originalTarget = mouseTarget;
        var originalTargets = mouseTargets;
        var originalQueueJob = queueJob;
        string originalStage = queueStage, originalStatus = status.Text, originalNotification = notificationStatus.Text;
        bool originalSubmitted = mouseSubmitted;
        var sent = new List<string>();
        var destinations = new List<string>();
        var gates = new List<TaskCompletionSource<string>>();
        settings.Telegram = new TelegramSettings { Enabled = true, BotToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZaBcDeFg", ChatId = "123456789",
            StartNotificationsEnabled = true, ProgressNotificationsEnabled = true, ErrorNotificationsEnabled = true, ProgressEveryQueries = 50 };
        try
        {
            SetAutomationReportTestSink((destination, message) => {
                destinations.Add(destination.ChatId); sent.Add(message); return Task.FromResult(TelegramNotifier.SuccessMessage);
            });
            PrepareAutomationReports("모의 자동 예매");
            mouseTarget = new BookingTarget { Date = "2099-01-02", DepartureStation = "서울", ArrivalStation = "전주",
                Train = "KTX 541", Departure = "12:00", Arrival = "14:00", Grade = "일반실", Passengers = 1 };
            mouseTargets = new List<BookingTarget> { mouseTarget };
            status.Text = "선택 열차 매진 · 다음 조회 대기";
            AutomationReportsStarted(); AutomationReportsStarted();
            await automationReportDrain;
            Assert(sent.Count == 1 && sent[0].Contains("자동 예매 시작") && sent[0].Contains("추가 조회 시도: 0회"),
                "automation report host: exactly one start with zero additional attempts");
            settings.Telegram.ChatId = "987654321";
            for (int i = 0; i < 49; i++) RecordAutomationQueryAttempt();
            ReportAutomationProgress();
            Assert(sent.Count == 1, "automation report host: no progress before the configured fiftieth attempt");
            queueRunning = true; queueStage = "booking";
            queueJob = new BookingQueueJob { Id = "report-job-a", Targets = mouseTargets, RefreshCount = 49 };
            RecordAutomationQueryAttempt(); queueJob.RefreshCount = 50;
            ReportAutomationProgress(); ReportAutomationProgress();
            await automationReportDrain;
            Assert(sent.Count == 2 && sent[1].Contains("추가 조회 시도: 50회") && destinations.All(value => value == "123456789"),
                "automation report host: fiftieth confirmed observation is deduplicated and uses captured destination");
            queueJob = new BookingQueueJob { Id = "report-job-b", Targets = mouseTargets, RefreshCount = 0 };
            for (int i = 0; i < 50; i++) RecordAutomationQueryAttempt();
            ReportAutomationProgress(); await automationReportDrain;
            Assert(sent.Count == 3 && sent[2].Contains("추가 조회 시도: 100회") && sent[2].Contains("예약 순서: 2 / 2"),
                "automation report host: report count continues across queue jobs without inheriting stored attempt totals");
            queueRunning = false; queueJob = null; queueStage = null;
            StopAutomationReports();

            sent.Clear();
            var heldStart = new TaskCompletionSource<string>(); gates.Add(heldStart);
            SetAutomationReportTestSink((destination, message) => { sent.Add(message); return heldStart.Task; });
            PrepareAutomationReports("모의 자동 예매"); AutomationReportsStarted();
            for (int i = 0; i < 50; i++) RecordAutomationQueryAttempt();
            ReportAutomationProgress();
            for (int i = 0; i < 50; i++) RecordAutomationQueryAttempt();
            ReportAutomationProgress();
            Assert(automationReportOutbox.Count == 1 && automationReportOutbox[0].Text.Contains("추가 조회 시도: 100회"),
                "automation report host: pending progress is bounded and coalesces to the latest milestone");
            StopAutomationReports();
            Assert(automationReportOutbox.Count == 0, "automation report host: user stop discards pending progress");
            heldStart.SetResult(TelegramNotifier.SuccessMessage); await automationReportDrain;
            Assert(sent.Count == 1, "automation report host: stopped progress is never sent after blocked transport completes");

            sent.Clear();
            var heldFailureStart = new TaskCompletionSource<string>(); gates.Add(heldFailureStart);
            SetAutomationReportTestSink((destination, message) => {
                sent.Add(message); return sent.Count == 1 ? heldFailureStart.Task : Task.FromResult(TelegramNotifier.SuccessMessage);
            });
            PrepareAutomationReports("모의 자동 예매"); AutomationReportsStarted();
            for (int i = 0; i < 50; i++) RecordAutomationQueryAttempt();
            ReportAutomationProgress();
            mouseSubmitted = true;
            var nativeFailure = new COMException("NEVER_INCLUDE_NATIVE_EXCEPTION_MESSAGE", unchecked((int)0x80004005));
            ReportAutomationFailure("브라우저 프로세스 종료: BrowserProcessExited", nativeFailure, "browser-process");
            ReportAutomationFailure("같은 브라우저 오류", nativeFailure, "browser-process");
            Assert(automationReportOutbox.Count == 1 && automationReportOutbox[0].Kind == "error",
                "automation report host: terminal error removes pending progress and is queued only once");
            string errorText = automationReportOutbox[0].Text;
            Assert(errorText.Contains("browser-process") && errorText.Contains("COMException") && errorText.Contains("0x80004005") &&
                errorText.Contains("다시 실행") && errorText.Contains("예약 결과 미확인") && errorText.Contains("공식 예약 내역") &&
                !errorText.Contains("NEVER_INCLUDE_NATIVE_EXCEPTION_MESSAGE"),
                "automation report host: browser error includes observed kind, safe runtime detail, restart action and submitted-result warning");
            StopAutomationReports();
            mouseSubmitted = false;
            PrepareAutomationReports("다음 모의 실행"); AutomationReportsStarted();
            heldFailureStart.SetResult(TelegramNotifier.SuccessMessage); await automationReportDrain;
            Assert(sent.Count == 3 && sent[1].Contains("오류로 중지") && sent[2].Contains("자동 예매 시작"),
                "automation report host: queued error survives stop and replacement run with priority over its start report");
            StopAutomationReports();

            sent.Clear();
            int failureCalls = 0;
            SetAutomationReportTestSink((destination, message) => {
                failureCalls++; sent.Add(message); throw new InvalidOperationException("mock transport failed");
            });
            notificationStatus.Text = "예약 완료 알림 표시 보존";
            int oldMouseGeneration = mouseGeneration, oldQueueGeneration = queueGeneration;
            PrepareAutomationReports("모의 준비 실패");
            ReportAutomationFailure("시작 조건을 확인하지 못했습니다.", new InvalidOperationException("모의 조회 조건 오류"));
            ReportAutomationFailure("중복 오류"); await automationReportDrain;
            Assert(failureCalls == 1 && sent.Count == 1 && sent[0].Contains("모의 조회 조건 오류"),
                "automation report host: preparation errors report before start and transport failures are not retried");
            Assert(!mouseBookingActive && !queueRunning && mouseGeneration == oldMouseGeneration && queueGeneration == oldQueueGeneration &&
                notificationStatus.Text == "예약 완료 알림 표시 보존" && automationReportStatus.Text.Contains("자동 재전송하지 않았습니다"),
                "automation report host: transport failure changes only report status, never booking state or reservation notification");
            StopAutomationReports();

            sent.Clear();
            SetAutomationReportTestSink((destination, message) => { sent.Add(message); return Task.FromResult(TelegramNotifier.SuccessMessage); });
            PrepareAutomationReports("모의 정상 중지");
            ReportAutomationFailure("F8로 중지했습니다.", new InvalidOperationException("F8로 중지했습니다."));
            ReportAutomationFailure("직접 입력이 감지돼 조회 조건 입력을 중지했습니다.");
            ReportAutomationFailure("예약 순서가 중지됐습니다.", new OperationCanceledException());
            Assert(sent.Count == 0, "automation report host: F8, user input and cancelled ownership do not produce errors");
            ReportAutomationFailure("다음 예약을 시작하지 못했습니다. F8로 중지했습니다.", new InvalidOperationException("F8로 중지했습니다."));
            ReportAutomationFailure("다음 예약을 시작하지 못했습니다. 직접 입력이 감지돼 조회 조건 입력을 중지했습니다.",
                new InvalidOperationException("직접 입력이 감지돼 조회 조건 입력을 중지했습니다."));
            Assert(sent.Count == 0, "automation report host: prefixed continuation errors still recognize the original F8 or direct-input interruption");
            ReportAutomationFailure("F8로 중지했습니다.", new OperationCanceledException(), "input-cleanup");
            await automationReportDrain;
            Assert(sent.Count == 1 && sent[0].Contains("입력 감시 정리 실패"), "automation report host: explicit cleanup error overrides normal cancellation filtering");
            StopAutomationReports();

            sent.Clear();
            var heldFinalStart = new TaskCompletionSource<string>(); gates.Add(heldFinalStart);
            SetAutomationReportTestSink((destination, message) => {
                sent.Add(message); return sent.Count == 1 ? heldFinalStart.Task : Task.FromResult(TelegramNotifier.SuccessMessage);
            });
            PrepareAutomationReports("모의 조회 한도"); AutomationReportsStarted();
            for (int i = 0; i < 50; i++) RecordAutomationQueryAttempt();
            ReportAutomationProgress(); StopAutomationReports(true);
            Assert(automationReportOutbox.Count == 1, "automation report host: normal limit completion retains the just-generated final milestone");
            heldFinalStart.SetResult(TelegramNotifier.SuccessMessage); await automationReportDrain;
            Assert(sent.Count == 2 && sent[1].Contains("추가 조회 시도: 50회"),
                "automation report host: final milestone still sends after normal query limit completion");

            sent.Clear();
            SetAutomationReportTestSink((destination, message) => { sent.Add(message); return Task.FromResult(TelegramNotifier.SuccessMessage); });
            PrepareAutomationReports("이전 모의 탐색");
            var oldReportOwner = automationReportContext;
            PrepareAutomationReports("새 모의 실행"); AutomationReportsStarted(); await automationReportDrain;
            var newReportOwner = automationReportContext;
            status.Text = "새 실행 상태 보존";
            int navigationMouseGeneration = mouseGeneration, navigationQueueGeneration = queueGeneration;
            bool navigationDocumentActive = activeDocument;
            HandleNavigationFailure(navigation - 1, newReportOwner, new InvalidOperationException("이전 페이지의 늦은 로딩 오류"));
            Assert(sent.Count == 1 && !newReportOwner.Ended && automationReportContext == newReportOwner &&
                status.Text == "새 실행 상태 보존" && mouseGeneration == navigationMouseGeneration &&
                queueGeneration == navigationQueueGeneration && activeDocument == navigationDocumentActive,
                "automation report host: stale navigation page cannot send an error or mutate the newer run");
            HandleNavigationFailure(navigation, oldReportOwner, new InvalidOperationException("이전 실행의 늦은 로딩 오류"));
            Assert(sent.Count == 1 && !newReportOwner.Ended && automationReportContext == newReportOwner &&
                status.Text == "새 실행 상태 보존" && mouseGeneration == navigationMouseGeneration &&
                queueGeneration == navigationQueueGeneration && activeDocument == navigationDocumentActive,
                "automation report host: stale report ownership cannot send an error or mutate the newer run");
            StopAutomationReports();

            SetAutomationReportTestSink(null);
            PrepareAutomationReports("실제 전송 없는 모의 검사"); AutomationReportsStarted(); await automationReportDrain;
            Assert(automationReportStatus.Text.Contains("실제 텔레그램 보고를 보내지 않습니다"),
                "automation report host: self-test without an injected sink suppresses all real Telegram traffic");
            StopAutomationReports();
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult(TelegramNotifier.SuccessMessage);
            StopAutomationReports();
            SetAutomationReportTestSink(null);
            settings.Telegram = originalTelegram; mouseTarget = originalTarget; mouseTargets = originalTargets;
            queueJob = originalQueueJob; queueStage = originalStage; queueRunning = false; mouseSubmitted = originalSubmitted;
            status.Text = originalStatus; notificationStatus.Text = originalNotification;
            automationReportContext = null; automationReportOutbox.Clear(); automationReportStatus.Text = "자동 예매 보고 모의 검사 완료";
        }
    }
}
