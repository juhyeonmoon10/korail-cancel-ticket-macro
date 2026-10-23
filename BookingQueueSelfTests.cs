using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    // Exercise host transitions with synthetic records only. Never call the
    // queue starter, input-watch starter, query timer or Telegram sender here.
    async Task RunBookingQueueSelfTests()
    {
        if (!Program.SelfTest) throw new InvalidOperationException("로컬 모의 검사 전용입니다.");
        Assert(!queueRunning && !queueBusy && !queueInputWatching && !mouseBookingActive && !mouseBookingBusy && !mouseResuming &&
            (queueTimer == null || !queueTimer.Enabled) && (mouseBookingTimer == null || !mouseBookingTimer.Enabled) && !sleepPrevention.Active,
            "queue host fixture: no running automation, input watch, timer or sleep session is inherited");
        var fields = typeof(MainWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => !field.IsInitOnly && (field.Name.StartsWith("queue", StringComparison.Ordinal) || field.Name.StartsWith("mouse", StringComparison.Ordinal)))
            .ToDictionary(field => field, field => field.GetValue(this));
        var progressFields = typeof(QueueQueryProgress).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(field => field, field => field.GetValue(queueProgress));
        string previousStatus = status.Text, previousNotification = notificationStatus.Text, previousSleepText = sleepStatus.Text;
        Color previousSleepColor = sleepStatus.ForeColor;
        int notificationsBefore = demoNotifications.Count;
        string sourceBefore = view.CoreWebView2.Source;
        string directory = Path.Combine(Program.DataRoot, "queue-self-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Keep cleanup paths inert even if an earlier fixture retained
            // inactive bookkeeping. EndInputWatch is safe with no installed hook.
            queueTimer = null; mouseBookingTimer = null; queueInputWatching = false;
            queueRunning = queueBusy = queueSubmitted = false; queueJob = null; queueStage = null;
            mouseBookingActive = mouseBookingBusy = mouseSubmitted = mousePaused = mouseResuming = false;
            mouseAutoRefresh = mouseRefreshOnly = mouseRefreshPending = mouseReadAll = mouseMorePending = false;
            mouseCompletionRecheck = false; mouseAttemptId = null; mouseTargets = null;
            queueStore = new BookingQueueStore(Path.Combine(directory, "queue.json"));
            var attempts = new BookingAttemptStore(Path.Combine(directory, "attempts.json"));
            var first = queueStore.Add(new[] { QueueFixtureTarget(541), QueueFixtureTarget(543) }, new RefreshOptions(5, 9999999, 20));
            var second = queueStore.Add(new[] { QueueFixtureTarget(601), QueueFixtureTarget(603) }, new RefreshOptions(7, 4000, 0));
            var third = queueStore.Add(new[] { QueueFixtureTarget(701) }, new RefreshOptions(3, 200, 10));
            queueJob = queueStore.BeginNext(); queueRunning = true; queueStage = "booking"; queueGeneration++;
            var firstAttempt = attempts.Begin(first.Targets[1]); BindQueuedAttempt(firstAttempt);
            Assert(queueJob.Id == first.Id && queueStore.Get(first.Id).AttemptId == firstAttempt.Id &&
                queueStore.Get(first.Id).AttemptTargetKey == first.Targets[1].Key,
                "queue host binding: the chosen alternative is persisted before its synthetic submission");
            ExpectQueueHostRejected(delegate { ConfirmQueuedBooking(firstAttempt); },
                "queue host confirmation: a prepared attempt cannot complete a job or advance the next one");
            attempts.MarkSubmitted(firstAttempt.Id); attempts.RecordUnknown(firstAttempt.Id, "synthetic unknown");
            ExpectQueueHostRejected(delegate { ConfirmQueuedBooking(attempts.Get(firstAttempt.Id)); },
                "queue host confirmation: unknown result does not count as one successful reservation");
            Assert(queueStage == "booking" && queueStore.Get(first.Id).State == BookingQueueState.Running && queueStore.Get(second.Id).State == BookingQueueState.Pending,
                "queue host rejection: uncertain receipt leaves the active item and subsequent order intact");
            attempts.RecordConfirmed(firstAttempt.Id, BookingReceipt.CreateConfirmed(firstAttempt.Target, firstAttempt.Target, "1234-5678", null));
            var firstConfirmed = attempts.Get(firstAttempt.Id);
            int firstGeneration = ConfirmQueuedBooking(firstConfirmed);
            Assert(firstGeneration == queueGeneration && queueStage == "notifying" && queueStore.Get(first.Id).State == BookingQueueState.Completed &&
                queueStore.Get(first.Id).CompletedTargetKey == first.Targets[1].Key && queueStore.Get(second.Id).State == BookingQueueState.Pending && attempts.GetAll().Count == 1,
                "queue host completion: one confirmed alternative completes one item while the next remains pending");
            mouseBookingActive = true; mousePhase = "submitted"; mouseStore = attempts; mouseAttemptId = firstAttempt.Id;
            mouseTarget = firstAttempt.Target.Copy(); mouseTargets = first.Targets.Select(target => target.Copy()).ToList();
            mouseSubmitted = false; // Same disarm order as the actual receipt branch.
            StopMouseBooking(null);
            Assert(!mouseBookingActive && queueRunning && queueStage == "notifying" && queueStore.Get(first.Id).State == BookingQueueState.Completed &&
                attempts.Get(firstAttempt.Id).State == BookingAttemptState.Confirmed,
                "queue host handoff: stopping native booking preserves the confirmed item and notification stage");
            ExpectQueueHostRejected(delegate { ConfirmQueuedBooking(firstConfirmed); },
                "queue host deduplication: repeated completion cannot consume the next queued item");
            Assert(!queueStore.Complete(first.Id, firstConfirmed) && queueStore.Get(second.Id).State == BookingQueueState.Pending && attempts.GetAll().Count == 1,
                "queue host deduplication: repeated durable receipt remains idempotent without another attempt");
            StopBookingQueue("모의 알림 대기 중 중지");
            ContinueQueueAfterNotification(firstGeneration);
            Assert(!queueRunning && queueJob == null && queueStage == null && queueGeneration != firstGeneration && queueStore.Get(second.Id).State == BookingQueueState.Pending,
                "queue host stop: a stopped generation cannot restart the remaining queue after notification");

            queueJob = queueStore.BeginNext(); queueRunning = true; queueStage = "booking"; queueGeneration++;
            mouseBookingActive = false;
            QueueBookingStopped("모의 예약 중지");
            Assert(!queueRunning && queueJob == null && queueStage == null && queueStore.Get(second.Id).State == BookingQueueState.NeedsReview && queueStore.Get(third.Id).State == BookingQueueState.Pending,
                "queue host interruption: native failure marks the current item for review and preserves later work");
            queueStore.ResetAfterUserConfirmed(second.Id, true);
            queueJob = queueStore.BeginNext(); queueRunning = true; queueStage = "booking"; queueGeneration++;
            var secondAttempt = attempts.Begin(second.Targets[0]); BindQueuedAttempt(secondAttempt);
            attempts.MarkSubmitted(secondAttempt.Id);
            attempts.RecordConfirmed(secondAttempt.Id, BookingReceipt.CreateConfirmed(secondAttempt.Target, secondAttempt.Target, "2345-6789", null));
            int secondGeneration = ConfirmQueuedBooking(attempts.Get(secondAttempt.Id));
            ContinueQueueAfterNotification(firstGeneration);
            Assert(queueRunning && queueGeneration == secondGeneration && queueStage == "notifying" && queueJob.Id == second.Id && queueStore.Get(third.Id).State == BookingQueueState.Pending,
                "queue host late notification: an older generation cannot stop or advance a newer confirmed job");
            StopBookingQueue("모의 두 번째 알림 대기 중 중지");

            queueJob = queueStore.BeginNext(); queueRunning = true; queueStage = "booking"; queueGeneration++;
            mouseBookingActive = true; mousePaused = true; mousePhase = "select"; mouseSubmitted = false;
            mouseStore = attempts; mouseAttemptId = null; mouseTarget = third.Targets[0].Copy(); mouseTargets = third.Targets.Select(target => target.Copy()).ToList();
            StopBookingQueue("모의 최소화 중지");
            Assert(!queueRunning && !mouseBookingActive && !mousePaused && mouseTargets == null && !mouseContinueButton.Enabled &&
                queueStore.Get(third.Id).State == BookingQueueState.NeedsReview,
                "queue host minimize regression: stopping the queue also disarms a resumable native child");
            Assert(queueJob == null && queueStage == null && !queueSubmitted && !queueInputWatching && queueTimer == null && mouseBookingTimer == null,
                "queue host cleanup: no queued intent, timer or input watch survives a stop");
            Assert(queueStore.Get(first.Id).State == BookingQueueState.Completed && queueStore.Get(second.Id).State == BookingQueueState.Completed &&
                attempts.GetAll().Count == 2 && attempts.GetAll().All(attempt => attempt.State == BookingAttemptState.Confirmed),
                "queue host one-per-item: exactly two successful items have exactly two confirmed attempts");

            var preview = queueStore.GetAll().Select(job => job.Copy()).ToList();
            preview[1].State = preview[2].State = BookingQueueState.Pending;
            using (var dialog = new BookingQueueDialog(preview))
            {
                dialog.Opacity = 0; dialog.ShowInTaskbar = false;
                dialog.ActionHandler = delegate(BookingQueueDialogAction action, string id) { return Task.FromResult((IList<BookingQueueJob>)preview); };
                dialog.Show(this); dialog.PerformLayout(); await Task.Delay(50);
                Assert(dialog.LayoutFits(), "queue manager layout: default window keeps every action and reservation table visible");
                SaveQueueDialogPreview(dialog, "queue-manager-preview.png");
                dialog.Size = dialog.MinimumSize; dialog.PerformLayout(); await Task.Delay(50);
                Assert(dialog.LayoutFits(), "queue manager layout: minimum window keeps every action and reservation table visible");
                SaveQueueDialogPreview(dialog, "queue-manager-compact-preview.png");
                Assert(dialog.RequestedAction == BookingQueueDialogAction.None && dialog.SelectedJobId == second.Id,
                    "queue manager preview: opening and resizing never requests an actual booking");
                dialog.Close();
            }

            // The same host handoff must retain partial All progress and must
            // never consume an unrelated saved journey for a direct batch.
            queueStore = new BookingQueueStore(Path.Combine(directory, "all-queue.json"));
            var unrelated = queueStore.Add(new[] { QueueFixtureTarget(801) }, new RefreshOptions(5, 10, 0));
            var all = queueStore.Add(new[] { QueueFixtureTarget(811), QueueFixtureTarget(813) }, new RefreshOptions(5, 2, 0, true, BookingCompletionMode.All));
            queueJob = queueStore.BeginSelected(all.Id); queueOnlyJobId = all.Id;
            queueRunning = true; queueStage = "booking"; queueGeneration++;
            RecordQueuedRefreshCount((long)Int32.MaxValue + 2);
            var allFirst = attempts.Begin(all.Targets[1]); BindQueuedAttempt(allFirst);
            attempts.MarkSubmitted(allFirst.Id);
            attempts.RecordConfirmed(allFirst.Id, BookingReceipt.CreateConfirmed(allFirst.Target, allFirst.Target, "3456-7890", null));
            int allGeneration = ConfirmQueuedBooking(attempts.Get(allFirst.Id));
            Assert(queueStage == "notifying" && queueJob.State == BookingQueueState.Pending && queueJob.ConfirmedCount == 1 &&
                queueOnlyJobId == all.Id && queueJob.RemainingTargets().Single().Key == all.Targets[0].Key && queueJob.RefreshCount == (long)Int32.MaxValue + 2,
                "All host handoff: first receipt retains only remaining candidate and cumulative long count in same isolated batch");
            mouseBookingActive = true; mouseSubmitted = false; mouseStore = attempts; mouseAttemptId = allFirst.Id;
            StopMouseBooking(null);
            Assert(queueRunning && queueStage == "notifying" && queueStore.Get(unrelated.Id).State == BookingQueueState.Pending && queueOnlyJobId == all.Id,
                "All host handoff: disarming confirmed native child preserves isolated scope and unrelated queue items");
            StopBookingQueue("모의 전체 예약 알림 대기 중 중지");
            ContinueQueueAfterNotification(allGeneration);
            Assert(!queueRunning && queueOnlyJobId == null && queueStore.Get(all.Id).State == BookingQueueState.Pending && queueStore.Get(all.Id).ConfirmedCount == 1 &&
                queueStore.Get(unrelated.Id).State == BookingQueueState.Pending,
                "All host stop: late notification cannot restart batch or earlier saved journeys and confirmed progress survives");

            queueJob = queueStore.BeginSelected(all.Id); queueOnlyJobId = all.Id;
            queueRunning = true; queueStage = "booking"; queueGeneration++;
            mouseAutoRefresh = true; mouseRefreshOnly = false; mouseRefreshQuery = queueJob.QueryJson;
            mouseTargets = queueJob.RemainingTargets();
            var resumed = QueuedMouseResumeSelection(json.Deserialize<Dictionary<string, object>>(queueJob.QueryJson));
            Assert(resumed.Count == 1 && resumed[0].Key == all.Targets[0].Key,
                "All host resume: durable full selection validates against only the still-unbooked native candidate");
            mouseTargets = all.Targets.Select(target => target.Copy()).ToList();
            ExpectQueueHostRejected(delegate { QueuedMouseResumeSelection(json.Deserialize<Dictionary<string, object>>(queueJob.QueryJson)); },
                "All host resume: reintroducing an already confirmed candidate fails before any input");
            mouseTargets = resumed;
            var allLast = attempts.Begin(resumed[0]); BindQueuedAttempt(allLast);
            ExpectQueueHostRejected(delegate { ConfirmQueuedBooking(attempts.Get(allFirst.Id)); },
                "All host late receipt: a prior confirmed candidate cannot interrupt or advance the next bound attempt");
            attempts.MarkSubmitted(allLast.Id);
            attempts.RecordConfirmed(allLast.Id, BookingReceipt.CreateConfirmed(allLast.Target, allLast.Target, "4567-8901", null));
            ConfirmQueuedBooking(attempts.Get(allLast.Id));
            Assert(queueJob.State == BookingQueueState.Completed && queueJob.ConfirmedCount == 2 && queueJob.RemainingTargets().Count == 0 &&
                queueStore.Get(unrelated.Id).State == BookingQueueState.Pending,
                "All host completion: last distinct receipt finishes entire batch without consuming unrelated saved work");
            StopBookingQueue("모의 전체 예약 검사 완료");
            Assert(demoNotifications.Count == notificationsBefore && view.CoreWebView2.Source == sourceBefore && !sleepPrevention.Active,
                "queue host tests: no navigation, live reservation, Telegram message or sleep session is started");
        }
        finally
        {
            // No active timers/hooks existed on entry; only synthetic inactive
            // references are restored. Also restore the readonly progress object.
            queueRunning = false; queueInputWatching = false;
            mouseBookingActive = false; mouseSubmitted = false;
            foreach (var entry in fields) entry.Key.SetValue(this, entry.Value);
            foreach (var entry in progressFields) entry.Key.SetValue(queueProgress, entry.Value);
            status.Text = previousStatus; notificationStatus.Text = previousNotification;
            sleepStatus.Text = previousSleepText; sleepStatus.ForeColor = previousSleepColor;
            ShowMouseContinue(); UpdateShellState();
        }
    }

    static BookingTarget QueueFixtureTarget(int number)
    {
        return new BookingTarget { Date = "2026-10-25", DepartureStation = "서울", ArrivalStation = "전주", Train = "KTX " + number,
            Departure = "06:00", Arrival = "07:45", Grade = "일반실", Passengers = 1 };
    }
    void ExpectQueueHostRejected(Action action, string name)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, name);
    }
    static void SaveQueueDialogPreview(BookingQueueDialog dialog, string name)
    {
        using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
        {
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(Program.TestOutput), name), ImageFormat.Png);
        }
    }
}
