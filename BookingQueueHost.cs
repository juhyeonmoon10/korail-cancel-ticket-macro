using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    BookingQueueStore queueStore;
    BookingQueueJob queueJob;
    bool queueRunning, queueBusy, queueInputWatching, queueSubmitted;
    int queueGeneration, queueActions, queueReaderPage = -1;
    uint queueInputVersion;
    string queueStage, queuePicker, queueLastAction, queueOnlyJobId;
    DateTime queueDeadline, queueNextInput;
    System.Windows.Forms.Timer queueTimer;
    readonly QueueQueryProgress queueProgress = new QueueQueryProgress();

    BookingQueueStore QueueStore()
    {
        if (queueStore == null) queueStore = new BookingQueueStore(Path.Combine(Program.DataRoot, "booking-queue.json"));
        return queueStore;
    }

    async Task OpenBookingQueue()
    {
        if (!ready || closing) return;
        await StopHelper("예약 관리를 열었습니다. 실행할 순서와 후보 열차를 확인하세요.");
        try
        {
            using (var dialog = new BookingQueueDialog(QueueStore().GetAll()))
            {
                dialog.ActionHandler = async delegate(BookingQueueDialogAction action, string id)
                {
                    var store = QueueStore();
                    if (action == BookingQueueDialogAction.AddCurrent)
                    {
                        var selected = await ReadSelectionForQueue();
                        using (var options = new RefreshSettingsDialog(CurrentRefreshOptions(), Font, true))
                        {
                            if (options.ShowDialog(dialog) == DialogResult.OK)
                                store.Add(selected.Targets, options.SelectedOptions, selected.QueryJson);
                        }
                    }
                    else if (action == BookingQueueDialogAction.History) ShowBookingHistory();
                    else if (action == BookingQueueDialogAction.Remove) store.Remove(id);
                    else if (action == BookingQueueDialogAction.MoveUp || action == BookingQueueDialogAction.MoveDown)
                    {
                        var jobs = store.GetAll(); int index = jobs.ToList().FindIndex(job => job.Id == id);
                        store.Move(id, index + (action == BookingQueueDialogAction.MoveUp ? -1 : 1));
                    }
                    else if (action == BookingQueueDialogAction.Retry)
                    {
                        var job = store.Get(id);
                        var attempts = new BookingAttemptStore(Path.Combine(Program.DataRoot, "booking-attempts.json"));
                        var bound = job.AttemptId == null ? null : attempts.Get(job.AttemptId);
                        if (bound != null && bound.State == BookingAttemptState.Confirmed)
                            store.Complete(job.Id, bound);
                        else
                        {
                            if (job.RemainingTargets().Any(target => attempts.FindByTarget(target) != null))
                                throw new InvalidOperationException("이 열차에 예약 시도 기록이 있습니다. ‘예약 기록’에서 공식 예약 결과를 확인하고 기록을 정리하세요.");
                            if (MessageBox.Show(dialog, "이 항목의 공식 예약 내역에 미확인 예약이 없음을 확인했나요?\n확인하면 이 항목을 다시 대기 상태로 바꿉니다. 실제 예약을 취소하지는 않습니다.", "예약 결과 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                                store.ResetAfterUserConfirmed(id, true);
                        }
                    }
                    return store.GetAll();
                };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (dialog.RequestedAction == BookingQueueDialogAction.StartQueue) await StartBookingQueue();
                else if (dialog.RequestedAction == BookingQueueDialogAction.BookCurrent) await StartMouseBooking();
            }
        }
        catch (Exception ex) { status.Text = "예약 관리를 열지 못했습니다. " + ex.Message; }
    }

    async Task StartBookingQueue()
    {
        Task stopping = StopHelper("예약 순서를 준비합니다.");
        int startRun = mouseGeneration;
        await stopping;
        if (startRun != mouseGeneration || closing) return;
        try
        {
            if (!ready || closing || !AutomationAvailable()) return;
            if (Program.SelfTest || Program.ManualDiagnostic) throw new InvalidOperationException("연습·진단 모드에서는 실제 예약 순서를 실행하지 않습니다.");
            if (!settings.Telegram.Enabled || settings.Telegram.Validate() != null)
                throw new InvalidOperationException("설정에서 내 텔레그램 연결을 저장하고 알림을 켜세요.");
            if (!InputWindowReady()) throw new InvalidOperationException("도우미 창을 열고 시작하세요. 마우스 모드에서는 맨 앞으로 표시하세요.");
            var jobs = QueueStore().GetAll();
            if (!jobs.Any(job => job.State == BookingQueueState.Pending)) throw new InvalidOperationException("실행할 대기 항목을 먼저 추가하세요.");
            if (jobs.Any(job => job.State == BookingQueueState.NeedsReview)) throw new InvalidOperationException("확인이 필요한 항목을 먼저 예약 관리에서 확인하세요.");
            queueOnlyJobId = null; queueRunning = true; queueGeneration++;
            BeginNextQueuedBooking();
        }
        catch (Exception ex) { StopBookingQueue("예약 순서를 시작하지 못했습니다. " + ex.Message); status.Text = ex.Message; }
    }

    void BeginNextQueuedBooking()
    {
        if (!queueRunning || closing) return;
        if (!InputWindowReady()) throw new InvalidOperationException("입력 대상 창이 비활성 상태라 다음 예약을 시작하지 않았습니다.");
        var pending = QueueStore().GetAll().FirstOrDefault(job => job.State == BookingQueueState.Pending && (queueOnlyJobId == null || job.Id == queueOnlyJobId));
        if (pending == null)
        {
            StopBookingQueue(null);
            status.Text = "예약 완료 조건을 모두 충족했습니다 · 결제는 공식 화면에서 직접 하세요.";
            return;
        }
        if (String.CompareOrdinal(pending.Targets[0].Date, DateTime.Today.ToString("yyyy-MM-dd")) < 0)
            throw new InvalidOperationException("다음 예약이 과거 날짜입니다. 예약 관리에서 확인하세요.");
        var attempts = new BookingAttemptStore(Path.Combine(Program.DataRoot, "booking-attempts.json"));
        if (pending.RemainingTargets().Any(target => attempts.FindByTarget(target) != null))
            throw new InvalidOperationException("다음 항목에 이미 시도한 열차가 있습니다. 예약 기록을 먼저 확인하세요.");
        // Validate before starting so an expired or already-booked pending item
        // stays editable. Persist Running before the first navigation or input.
        queueJob = queueOnlyJobId == null ? QueueStore().Begin(pending.Id) : QueueStore().BeginSelected(pending.Id);
        var selectionJob = queueJob.Copy(); selectionJob.Targets = queueJob.RemainingTargets();
        savedSelection = SavedTrainSelection.FromJob(selectionJob); savedSelection.Save(SelectionFile);
        queueStage = "query"; queueSubmitted = false; queuePicker = queueLastAction = null;
        queueProgress.Reset(); queueActions = 0; queueReaderPage = -1;
        queueDeadline = DateTime.UtcNow.AddSeconds(30); queueNextInput = DateTime.UtcNow;
        sleepPrevention.Start(); ShowSleepStatus(false);
        BeginAutomationInputWatch(); queueInputWatching = true;
        queueInputVersion = NativeMouseInput.ForeignInputVersion();
        EnsureQueueTimer();
        queueTimer.Start();
        status.Text = "다음 예약 조회 준비 · " + queueJob.Targets[0].Date + " · " + queueJob.Targets[0].DepartureStation + " → " + queueJob.Targets[0].ArrivalStation + " · F8 중지";
        UpdateShellState();
        Navigate("https://www.korail.com/ticket/search/general");
    }

    void EnsureQueueTimer()
    {
        if (queueTimer != null) return;
        queueTimer = new System.Windows.Forms.Timer { Interval = 500 };
        queueTimer.Tick += async delegate { await TickQueueQuery(); };
    }

    // The ordinary Start button can run all selected trains without consuming
    // any earlier saved journeys. Its progress is still visible/recoverable in
    // 예약 관리, and the durable attempt guards are shared with queued jobs.
    void StartSelectedBookingBatch(Dictionary<string, object> query)
    {
        if (queueRunning || queueJob != null) throw new InvalidOperationException("진행 중인 예약 순서를 먼저 중지하세요.");
        var store = QueueStore();
        if (store.GetAll().Any(job => job.State == BookingQueueState.Running || job.State == BookingQueueState.NeedsReview))
            throw new InvalidOperationException("예약 관리에서 확인이 필요한 항목을 먼저 확인하세요.");
        var created = store.Add(mouseTargets, CurrentRefreshOptions(), SavedTrainSelection.QueryText(query, mouseTargets));
        queueJob = store.BeginSelected(created.Id);
        queueOnlyJobId = created.Id; queueRunning = true; queueGeneration++; queueStage = "booking";
        EnsureQueueTimer();
    }

    void RecordQueuedRefreshCount(long count)
    {
        if (!queueRunning || queueJob == null) return;
        if (queueStage != "booking") throw new InvalidOperationException("실행 중인 예약의 조회 횟수를 확인하지 못했습니다.");
        QueueStore().RecordRefreshCount(queueJob.Id, count);
        queueJob.RefreshCount = count;
    }

    void ReleaseQueueInput()
    {
        if (queueTimer != null) queueTimer.Stop();
        if (queueInputWatching)
        {
            queueInputWatching = false;
            NativeMouseInput.EndInputWatch();
        }
        ReleaseSleepPrevention();
    }

    void StopBookingQueue(string reason)
    {
        if (!queueRunning && !queueInputWatching) return;
        bool stopNative = queueRunning && mouseBookingActive;
        queueRunning = false; queueGeneration++;
        try
        {
            if (queueJob != null)
            {
                var current = QueueStore().Get(queueJob.Id);
                if (current != null && current.State == BookingQueueState.Running) QueueStore().MarkNeedsReview(current.Id);
            }
        }
        catch { reason = "예약 순서의 중지 기록을 저장하지 못했습니다. 공식 예약 내역을 확인하세요."; }
        try { ReleaseQueueInput(); }
        catch { reason = "입력 감시를 정리하지 못했습니다. 프로그램을 다시 실행하세요."; }
        queueJob = null; queueOnlyJobId = null; queueStage = null; queuePicker = null; queueSubmitted = false; queueProgress.Reset();
        if (stopNative) StopMouseBooking(reason);
        if (!closing && reason != null) status.Text = reason + " · 예약 순서 중지";
        if (!closing) UpdateShellState();
    }

    void QueueBookingStopped(string reason)
    {
        if (queueRunning && queueStage == "booking") StopBookingQueue(reason ?? "예약 완료를 확인하기 전에 중지했습니다.");
    }

    void BindQueuedAttempt(BookingAttempt attempt)
    {
        if (!queueRunning) return;
        if (queueStage != "booking" || queueJob == null) throw new InvalidOperationException("실행 중인 예약 순서를 확인하지 못했습니다.");
        QueueStore().BindAttempt(queueJob.Id, attempt);
    }

    int ConfirmQueuedBooking(BookingAttempt attempt)
    {
        if (!queueRunning) return -1;
        if (queueStage != "booking" || queueJob == null) throw new InvalidOperationException("완료할 예약 순서를 확인하지 못했습니다.");
        var current = QueueStore().Get(queueJob.Id);
        if (attempt == null || current == null || current.AttemptId != attempt.Id || attempt.Target == null || current.AttemptTargetKey != attempt.Target.Key)
            throw new InvalidOperationException("현재 예약 시도와 다른 완료 기록으로 다음 열차를 시작할 수 없습니다.");
        if (!QueueStore().Complete(queueJob.Id, attempt)) throw new InvalidOperationException("이미 처리한 예약 완료 기록입니다.");
        queueJob = QueueStore().Get(queueJob.Id);
        queueStage = "notifying"; // Native stop must not turn a confirmed job into NeedsReview.
        return queueGeneration;
    }

    void ContinueQueueAfterNotification(int generation)
    {
        if (generation < 0 || generation != queueGeneration || !queueRunning || closing || queueStage != "notifying") return;
        try { CheckQueueInput(); ReleaseQueueInput(); BeginNextQueuedBooking(); }
        catch (Exception ex) { StopBookingQueue("다음 예약을 시작하지 못했습니다. " + ex.Message); }
    }

    void WatchQueueNotification(int generation)
    {
        if (generation < 0 || generation != queueGeneration || !queueRunning || queueStage != "notifying") return;
        sleepPrevention.Start(); ShowSleepStatus(false);
        BeginAutomationInputWatch(); queueInputWatching = true;
        queueInputVersion = NativeMouseInput.ForeignInputVersion();
        queueDeadline = DateTime.UtcNow.AddSeconds(60); queueTimer.Start();
    }

    bool QueueQueryCurrent(int run) { return queueRunning && queueStage == "query" && run == queueGeneration && !closing; }
    void CheckQueueInput()
    {
        if (NativeMouseInput.IsStopPressed()) throw new InvalidOperationException("F8로 중지했습니다.");
        if (!InputWindowReady())
            throw new InvalidOperationException("창이 바뀌어 조회 조건 입력을 중지했습니다.");
        if (NativeMouseInput.ForeignInputVersion() != queueInputVersion)
            throw new InvalidOperationException("직접 입력이 감지돼 조회 조건 입력을 중지했습니다.");
        if (!AutomationAvailable()) throw new InvalidOperationException(status.Text);
        if (DateTime.UtcNow > queueDeadline) throw new InvalidOperationException(queueStage == "notifying" ? "알림 처리 기한을 넘어 다음 예약을 중지했습니다." : "30초 안에 다음 조회 단계를 확인하지 못했습니다.");
    }

    async Task<Dictionary<string, object>> ReadQueueQuery()
    {
        string source = view.CoreWebView2.Source;
        if (!OfficialTicketSource(source)) throw new InvalidOperationException("공식 코레일 조회 화면을 벗어났습니다. 로그인·인증을 직접 확인하세요.");
        int page = navigation;
        if (queueReaderPage != page || !await EvalBool("!!window.KorailQueueQuery"))
        {
            await view.CoreWebView2.ExecuteScriptAsync(File.ReadAllText(Path.Combine(Program.BundleRoot, "core.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "korail-site.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "queue-query.js")));
            if (page == navigation) queueReaderPage = page;
        }
        if (queueJob == null || navigationPending || page != navigation || source != view.CoreWebView2.Source) return null;
        await view.CoreWebView2.ExecuteScriptAsync("(() => { const r=document.getElementById('korail-one-helper')?.shadowRoot; const m=r?.querySelector('main'); if(m){m.hidden=true;r.getElementById('fold').textContent='+';} })()");
        if (queueJob == null || navigationPending || page != navigation || source != view.CoreWebView2.Source) return null;
        string raw = await view.CoreWebView2.ExecuteScriptAsync("window.KorailQueueQuery.read(" + queueJob.QueryJson + ",null," + json.Serialize(new { submitted = queueSubmitted, activePicker = queuePicker, latestDeparture = queueJob.RemainingTargets().Min(target => target.Departure) }) + ")");
        if (navigationPending || page != navigation || source != view.CoreWebView2.Source) return null;
        return json.Deserialize<Dictionary<string, object>>(raw);
    }

    async Task TickQueueQuery()
    {
        if (!queueRunning || queueBusy || closing) return;
        if (queueStage == "notifying")
        {
            try { CheckQueueInput(); }
            catch (Exception ex) { StopBookingQueue(ex.Message); }
            return;
        }
        if (queueStage != "query") return;
        queueBusy = true; int run = queueGeneration;
        try
        {
            CheckQueueInput();
            if (navigationPending || DateTime.UtcNow < queueNextInput) return;
            int plannedPage = navigation; string plannedSource = view.CoreWebView2.Source;
            var plan = await ReadQueueQuery();
            if (!QueueQueryCurrent(run) || plan == null) return;
            if (!String.IsNullOrEmpty(Get(plan, "blockCode"))) ObserveSiteBlock(Get(plan, "blockCode"));
            if (!QueueQueryCurrent(run)) return;
            string state = Get(plan, "state");
            if (state == "blocked") throw new InvalidOperationException(Get(plan, "message"));
            if (Object.Equals(plan.ContainsKey("pickerClosed") ? plan["pickerClosed"] : null, true)) queuePicker = null;
            if (state == "ready")
            {
                if (!queueSubmitted || !queueProgress.Ready(navigation, view.CoreWebView2.Source, NumberValue(plan, "resultRevision"), Get(plan, "fingerprint"), DateTime.UtcNow)) return;
                var observedQuery = ObjectValue(plan, "query");
                var remainingTargets = queueJob.RemainingTargets();
                string effectiveQuery = SavedTrainSelection.QueryText(observedQuery, remainingTargets);
                var expectedQuery = json.Deserialize<Dictionary<string, object>>(queueJob.QueryJson);
                string effectiveTime = Get(observedQuery, "fromTime");
                if (String.CompareOrdinal(effectiveTime, Get(expectedQuery, "fromTime")) < 0 || String.CompareOrdinal(effectiveTime, remainingTargets.Min(target => target.Departure)) > 0)
                    throw new InvalidOperationException("다음 조회 시작 시간이 후보 열차의 출발 시각을 벗어났습니다.");
                expectedQuery["fromTime"] = effectiveTime;
                if (effectiveQuery != SavedTrainSelection.QueryText(expectedQuery, remainingTargets))
                    throw new InvalidOperationException("다음 예약의 저장 조건과 실제 조회 결과가 다릅니다.");
                CheckQueueInput(); ReleaseQueueInput(); queueStage = "starting";
                var starting = queueJob.Copy();
                starting.Targets = remainingTargets;
                starting.QueryJson = effectiveQuery;
                savedSelection = SavedTrainSelection.FromJob(starting); savedSelection.Save(SelectionFile);
                await RestoreSavedSelection();
                if (!queueRunning || run != queueGeneration || closing) return;
                mouseRefreshIntervalSeconds = starting.IntervalSeconds; mouseRefreshLimit = starting.Limit; mouseRefreshJitterPercent = starting.JitterPercent;
                mouseRefreshUnlimited = starting.Unlimited; mouseBookingCompletionMode = starting.CompletionMode;
                await StartMouseBooking(true, starting);
                if (!queueRunning || run != queueGeneration || closing) return;
                if (!mouseBookingActive) throw new InvalidOperationException(status.Text);
                queueStage = "booking"; UpdateShellState(); return;
            }
            if (state == "unknown" || state == "waiting")
            {
                queueProgress.Unstable();
                status.Text = "다음 예약 조회 · " + Get(plan, "message"); return;
            }
            var action = ObjectValue(plan, "action");
            if (state != "action" || action == null) throw new InvalidOperationException("조회 단계를 확인하지 못했습니다.");
            if (queueSubmitted)
            {
                if (Object.Equals(plan.ContainsKey("loading") ? plan["loading"] : null, true) || NumberValue(plan, "resultCount") < 1)
                { queueProgress.Unstable(); return; }
                if (!queueProgress.Ready(navigation, view.CoreWebView2.Source, NumberValue(plan, "resultRevision"), Get(plan, "fingerprint"), DateTime.UtcNow)) return;
            }
            string kind = Get(action, "kind"), id = Get(action, "id");
            if (kind != "click" && kind != "type" && kind != "submit-query") throw new InvalidOperationException("알 수 없는 조회 입력입니다.");
            double x = NumberValue(action, "x"), y = NumberValue(action, "y"), width = NumberValue(action, "viewportWidth"), height = NumberValue(action, "viewportHeight");
            if (width <= 0 || height <= 0 || x < 0 || x >= width) throw new InvalidOperationException("조회 버튼의 위치를 확인하세요.");
            var origin = view.PointToScreen(Point.Empty); var viewport = view.ClientSize;
            var fresh = await ReadQueueQuery(); var freshAction = ObjectValue(fresh, "action");
            if (!QueueQueryCurrent(run)) return;
            if (MouseDocumentChanged(plannedPage, plannedSource)) return;
            if (freshAction == null || Get(freshAction, "id") != id || Get(freshAction, "kind") != kind ||
                Get(freshAction, "text") != Get(action, "text") || Math.Abs(NumberValue(freshAction, "x") - x) > 1 || Math.Abs(NumberValue(freshAction, "y") - y) > 1 ||
                NumberValue(freshAction, "viewportWidth") != width || NumberValue(freshAction, "viewportHeight") != height ||
                origin != view.PointToScreen(Point.Empty) || viewport != view.ClientSize) return;
            if (queueSubmitted)
            {
                if (Object.Equals(fresh.ContainsKey("loading") ? fresh["loading"] : null, true) || NumberValue(fresh, "resultCount") < 1)
                { queueProgress.Unstable(); return; }
                if (!queueProgress.Ready(navigation, view.CoreWebView2.Source, NumberValue(fresh, "resultRevision"), Get(fresh, "fingerprint"), DateTime.UtcNow)) return;
            }
            CheckQueueInput();
            if (y < 8 || y >= height - 8)
            {
                if (++queueActions > 100) throw new InvalidOperationException("다음 조회의 입력 단계가 너무 많아 중지했습니다.");
                await WheelAutomation(y < 8 ? 360 : -360, run, true);
                if (!QueueQueryCurrent(run)) return;
                queueNextInput = DateTime.UtcNow.AddMilliseconds(600); return;
            }
            if (id == queueLastAction) return; // Wait for the observed control state to change; never double-submit.
            if (++queueActions > 100) throw new InvalidOperationException("다음 조회의 입력 단계가 너무 많아 중지했습니다.");
            // Repeated clicks on one unchanged control do not extend its deadline.
            if (id != queueLastAction) { queueDeadline = DateTime.UtcNow.AddSeconds(30); queueLastAction = id; }
            if (kind == "submit-query")
            {
                queueSubmitted = true;
                queueProgress.Submit(navigation, view.CoreWebView2.Source, NumberValue(fresh, "resultRevision"));
            }
            else { queueSubmitted = false; queueProgress.Reset(); }
            if (!String.IsNullOrEmpty(Get(freshAction, "picker"))) queuePicker = Get(freshAction, "picker");
            // Keep ownership until the popup is observed closed. A slow closing
            // animation must not turn our own picker into an unknown user popup.
            if (kind == "type") await TypeStationAutomation(Get(freshAction, "text"), run);
            else await ClickAutomation(x, y, width, height, run, true);
            if (!QueueQueryCurrent(run)) return;
            queueNextInput = DateTime.UtcNow.AddMilliseconds(900);
            status.Text = "다음 예약의 날짜·구간 입력 중 · F8 중지";
        }
        catch (Exception ex) { if (QueueQueryCurrent(run) || queueRunning && run == queueGeneration) StopBookingQueue(ex.Message); }
        finally { queueBusy = false; }
    }
}
