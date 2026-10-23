using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// Local persistence and state transitions only: never contact Korail/Telegram.
internal static class BookingQueueTests
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (InvalidDataException) { return true; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
    static BookingTarget Target(int number)
    {
        return new BookingTarget { Date = "2026-10-25", DepartureStation = "서울", ArrivalStation = "부산", Train = "KTX " + number, Departure = "07:30", Arrival = "10:15", Grade = "일반실", Passengers = 1 };
    }
    static RefreshOptions Options() { return new RefreshOptions(5, 9999999, 20); }
    static BookingAttempt Confirm(BookingAttemptStore attempts, BookingAttempt attempt)
    {
        attempts.MarkSubmitted(attempt.Id);
        attempts.RecordConfirmed(attempt.Id, BookingReceipt.CreateConfirmed(attempt.Target, attempt.Target, "1234-5678", "2026-10-24 23:59"));
        return attempts.Get(attempt.Id);
    }
    static int Main(string[] args)
    {
        try
        {
            string parent = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "korail-queue-tests") : args[0];
            string directory = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            Run(directory); Console.WriteLine("PASS " + count + " booking queue checks (local files only)"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run(string directory)
    {
        RunAllCandidates(directory);
        string path = Path.Combine(directory, "queue.json");
        var queue = new BookingQueueStore(path);
        Check(queue.GetAll().Count == 0 && !File.Exists(path), "empty queue read creates no persistence or bookings");
        var original = Target(101);
        var first = queue.Add(new[] { original, Target(103) }, Options());
        var second = queue.Add(new[] { Target(201) }, new RefreshOptions(Int32.MaxValue, Int32.MaxValue, 100));
        Check(first.Id != second.Id && queue.GetAll().Select(j => j.Id).SequenceEqual(new[] { first.Id, second.Id }), "ordered jobs have stable distinct identities");
        Check(queue.Get(first.Id).IntervalSeconds == 5 && queue.Get(first.Id).Limit == 9999999 && queue.Get(second.Id).Limit == Int32.MaxValue && queue.Get(second.Id).IntervalSeconds == Int32.MaxValue, "queue preserves low intervals and large limits without clamp");
        original.Train = "KTX 999"; first.Targets[0].Train = "KTX 888";
        var returned = queue.GetAll(); returned[0].Targets.Clear(); returned.Clear();
        Check(queue.Get(first.Id).Targets.Count == 2 && queue.Get(first.Id).Targets[0].Train == "KTX 101", "inputs and returned snapshots cannot mutate saved targets");
        Check(queue.Get(first.Id).State == BookingQueueState.Pending && new BookingQueueStore(path).Get(first.Id).State == BookingQueueState.Pending, "pending queue survives restart without starting itself");
        Check(Reject(() => queue.Begin(second.Id)), "cannot skip queue ordering through Begin(id)");
        queue.Move(second.Id, 0);
        Check(queue.GetAll()[0].Id == second.Id, "pending job can move before another pending job");
        queue.Move(second.Id, 1);
        Check(Reject(() => queue.Move(second.Id, -1)) && queue.GetAll()[1].Id == second.Id, "invalid reorder leaves prior order intact");
        var running = queue.BeginNext();
        Check(running.Id == first.Id && queue.Get(first.Id).State == BookingQueueState.Running && queue.Get(first.Id).StartedUtc != null, "begin is durable before any external execution");
        Check(Reject(() => queue.BeginNext()) && Reject(() => queue.Begin(second.Id)), "one running job blocks all further starts");
        Check(Reject(() => queue.Move(first.Id, 1)) && Reject(() => queue.Remove(first.Id)), "active job cannot be removed or reordered");
        var attempts = new BookingAttemptStore(Path.Combine(directory, "attempts.json"));
        var wrong = attempts.Begin(Target(901));
        Check(Reject(() => queue.BindAttempt(first.Id, wrong)), "attempt outside frozen alternative set is rejected");
        var attempt = attempts.Begin(Target(103));
        queue.BindAttempt(first.Id, attempt); queue.BindAttempt(first.Id, attempt);
        Check(queue.Get(first.Id).AttemptId == attempt.Id, "matching prepared alternative binds durably and idempotently");
        var another = attempts.Begin(Target(101));
        Check(Reject(() => queue.BindAttempt(first.Id, another)), "one job cannot start a second alternative after an attempt is bound");
        Check(Reject(() => queue.Complete(first.Id, attempt)), "prepared reservation cannot complete a job");
        attempts.MarkSubmitted(attempt.Id);
        Check(Reject(() => queue.Complete(first.Id, attempts.Get(attempt.Id))), "submitted click alone cannot complete a job");
        attempts.RecordUnknown(attempt.Id, "SYNTHETIC_RAW_PRIVATE_ERROR");
        Check(Reject(() => queue.Complete(first.Id, attempts.Get(attempt.Id))) && queue.Get(first.Id).State == BookingQueueState.Running, "unknown reservation never advances the queue");
        queue = new BookingQueueStore(path);
        Check(queue.Get(first.Id).State == BookingQueueState.NeedsReview && queue.Get(first.Id).AttemptId == attempt.Id && queue.Get(second.Id).State == BookingQueueState.Pending, "restart freezes interrupted job with attempt binding and preserves next job");
        Check(Reject(() => queue.BeginNext()), "interrupted job cannot auto-resubmit or skip to next job");
        Check(Reject(() => queue.ResetAfterUserConfirmed(first.Id, false)), "review reset requires explicit user confirmation");
        attempts.RecordConfirmed(attempt.Id, BookingReceipt.CreateConfirmed(attempt.Target, attempt.Target, "1234-5678", "2026-10-24 23:59"));
        attempts.BeginNotification(attempt.Id); attempts.CompleteNotification(attempt.Id, false, "PRIVATE_TOKEN");
        var confirmed = attempts.Get(attempt.Id);
        var mismatched = confirmed.Copy(); mismatched.Target = Target(101); mismatched.Receipt = BookingReceipt.CreateConfirmed(mismatched.Target, mismatched.Target, "1234-5678", null);
        Check(Reject(() => queue.Complete(second.Id, confirmed)), "confirmation cannot complete a different unbound job");
        var badDate = confirmed.Copy(); badDate.Target.Date = "2026-10-26";
        Check(Reject(() => queue.Complete(first.Id, badDate)), "receipt and full target identity must agree");
        Check(queue.Complete(first.Id, confirmed), "confirmed reservation completes review job even if Telegram notification failed");
        Check(queue.Get(first.Id).CompletedTargetKey == Target(103).Key && queue.Get(first.Id).State == BookingQueueState.Completed && queue.Get(first.Id).Targets.Count == 2, "one successful alternative completes the entire multi-train job");
        Check(!queue.Complete(first.Id, confirmed) && queue.Get(second.Id).State == BookingQueueState.Pending, "duplicate completion neither repeats success nor begins next job");
        Check(Reject(() => queue.Complete(first.Id, mismatched)), "another alternative cannot replace the completed target");
        Check(!File.ReadAllText(path).Contains("PRIVATE_TOKEN") && !File.ReadAllText(path).Contains("SYNTHETIC_RAW_PRIVATE_ERROR"), "queue contains no raw notification or site errors");
        Check(queue.BeginNext().Id == second.Id, "next job can start only after confirmed completion");
        queue.MarkNeedsReview(second.Id);
        queue.ResetAfterUserConfirmed(second.Id, true);
        Check(queue.Get(second.Id).State == BookingQueueState.Pending && queue.Get(second.Id).StartedUtc == null && queue.Get(second.Id).AttemptId == null, "explicit review reset returns job to pending without releasing attempt store locks");
        Check(attempts.Get(attempt.Id).IsLocked, "queue actions preserve independent duplicate reservation locks");
        queue.Remove(first.Id);
        Check(queue.Get(first.Id) == null && attempts.Get(attempt.Id).State == BookingAttemptState.Confirmed, "removing completed history never cancels or changes a real reservation");
        queue.Remove(second.Id);
        Check(queue.BeginNext() == null && queue.GetAll().Count == 0, "finished empty queue returns no work");

        var normal = new BookingQueueStore(Path.Combine(directory, "normal.json"));
        var normalJob = normal.Add(new[] { Target(301) }, Options()); normal.Begin(normalJob.Id);
        var normalAttempt = attempts.Begin(Target(301)); normal.BindAttempt(normalJob.Id, normalAttempt);
        Check(normal.Complete(normalJob.Id, Confirm(attempts, normalAttempt)) && new BookingQueueStore(Path.Combine(directory, "normal.json")).Get(normalJob.Id).State == BookingQueueState.Completed, "ordinary complete transition persists across restart");
        Check(Reject(() => normal.BindAttempt(normalJob.Id, normalAttempt)) && Reject(() => normal.MarkNeedsReview(normalJob.Id)) && Reject(() => normal.ResetAfterUserConfirmed(normalJob.Id, true)), "completed job cannot be downgraded or rebound");

        var invalid = new BookingQueueStore(Path.Combine(directory, "input.json"));
        Check(Reject(() => invalid.Add(new BookingTarget[0], Options())) && Reject(() => invalid.Add(null, Options())) && Reject(() => invalid.Add(new[] { Target(401) }, null)), "missing targets or refresh settings rejected");
        Check(Reject(() => invalid.Add(new[] { Target(401), Target(401) }, Options())), "duplicate full target rejected");
        var alternate = Target(401); alternate.Departure = "08:30";
        Check(Reject(() => invalid.Add(new[] { Target(401), alternate }, Options())), "same train number at conflicting times rejected");
        alternate = Target(402); alternate.Grade = "특실";
        Check(Reject(() => invalid.Add(new[] { Target(401), alternate }, Options())), "mixed cabin grades in one job rejected");
        alternate = Target(402); alternate.Date = "2026-10-26";
        Check(Reject(() => invalid.Add(new[] { Target(401), alternate }, Options())), "different dates require separate jobs");
        alternate = Target(402); alternate.ArrivalStation = "대전";
        Check(Reject(() => invalid.Add(new[] { Target(401), alternate }, Options())), "different routes require separate jobs");
        alternate = Target(402); alternate.Passengers = 2;
        Check(Reject(() => invalid.Add(new[] { alternate }, Options())), "multi-passenger job rejected");
        Check(Reject(() => invalid.Add(Enumerable.Range(500, 21).Select(Target), Options())), "more than 20 train candidates rejected");
        Check(invalid.Add(Enumerable.Range(500, 20).Select(Target), Options()).Targets.Count == 20, "20 alternative trains accepted as one reservation job");
        for (int i = 1; i < BookingQueueStore.MaximumJobs; i++) invalid.Add(new[] { Target(600 + i) }, Options());
        Check(invalid.GetAll().Count == 20 && Reject(() => invalid.Add(new[] { Target(999) }, Options())), "maximum 20 ordered jobs enforced without truncation");

        string queryPath = Path.Combine(directory, "query.json"); var queryStore = new BookingQueueStore(queryPath);
        var queryJob = queryStore.Add(new[] { Target(701) }, Options());
        var serializer = new JavaScriptSerializer(); var query = serializer.Deserialize<Dictionary<string, object>>(queryJob.QueryJson);
        Check(Object.Equals(query["fromTime"], "07:30") && Object.Equals(query["seatAttribute"], "일반석"), "default query is derived from frozen journey");
        query["fromTime"] = "00:00"; query["seatAttribute"] = "유아동반석";
        var savedQuery = queryStore.Add(new[] { Target(703) }, Options(), serializer.Serialize(query));
        Check(savedQuery.QueryJson.Contains("00:00") && savedQuery.QueryJson.Contains("유아동반석") && new BookingQueueStore(queryPath).Get(savedQuery.Id).QueryJson == savedQuery.QueryJson, "full selected query hour and seat attribute survive restart");
        query["token"] = "SYNTHETIC_SECRET";
        Check(Reject(() => queryStore.Add(new[] { Target(705) }, Options(), serializer.Serialize(query))) && !File.ReadAllText(queryPath).Contains("SYNTHETIC_SECRET"), "unknown query fields rejected before persistence");
        query.Remove("token"); query["passengers"] = "1";
        Check(Reject(() => BookingQueueStore.NormalizeQuery(serializer.Serialize(query), new[] { Target(701) })), "query scalar types must match observed schema");
        query["passengers"] = 1; query["date"] = "2026-10-26";
        Check(Reject(() => queryStore.Add(new[] { Target(705) }, Options(), serializer.Serialize(query))), "query date must match frozen targets");
        query["date"] = "2026-10-25"; query["fromTime"] = "08:00";
        Check(Reject(() => queryStore.Add(new[] { Target(705) }, Options(), serializer.Serialize(query))), "query hour cannot exclude a selected train");
        query["fromTime"] = "00:00"; query["roundTrip"] = true;
        Check(Reject(() => queryStore.Add(new[] { Target(705) }, Options(), serializer.Serialize(query))), "unsupported round trip query rejected");
        Check(Reject(() => queryStore.Add(new[] { Target(705) }, Options(), new string('x', 8001))) && Reject(() => queryStore.Add(new[] { Target(705) }, Options(), "[]")), "oversized or non-object query rejected");
        query["roundTrip"] = false; query["seatAttribute"] = "일반석\n";
        Check(Reject(() => queryStore.Add(new[] { Target(705) }, Options(), serializer.Serialize(query))), "control characters in query text rejected");

        string before = File.ReadAllText(queryPath);
        using (var locked = new FileStream(queryPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Reject(() => queryStore.BeginNext()), "failed durable write lock prevents running transition");
        Check(File.ReadAllText(queryPath) == before && queryStore.Get(queryJob.Id).State == BookingQueueState.Pending, "failed mutation preserves the last good queue");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic queue writes leave no temporary files");
        File.WriteAllText(queryPath, "{truncated", Encoding.UTF8);
        Check(Reject(() => queryStore.BeginNext()) && File.ReadAllText(queryPath) == "{truncated", "corrupt queue fails closed without overwriting evidence");
        Check(Reject(() => new BookingQueueStore(queryPath)), "restart cannot turn corrupt queue into empty queue");
        File.WriteAllText(queryPath, "{\"Version\":999,\"Jobs\":[]}", Encoding.UTF8);
        Check(Reject(() => new BookingQueueStore(queryPath)), "future queue schema version fails closed");
        File.WriteAllText(queryPath, before, Encoding.UTF8);
        var corrupt = serializer.Deserialize<BookingQueueStore.StoredQueue>(before);
        corrupt.Jobs[1].Id = corrupt.Jobs[0].Id;
        File.WriteAllText(queryPath, serializer.Serialize(corrupt), Encoding.UTF8);
        Check(Reject(() => new BookingQueueStore(queryPath)), "duplicate persisted job IDs rejected");
        corrupt = serializer.Deserialize<BookingQueueStore.StoredQueue>(before); corrupt.Jobs[0].State = BookingQueueState.Completed;
        File.WriteAllText(queryPath, serializer.Serialize(corrupt), Encoding.UTF8);
        Check(Reject(() => new BookingQueueStore(queryPath)), "forged completed state without durable attempt rejected");
        File.WriteAllText(queryPath, new string(' ', 1048577), Encoding.UTF8);
        Check(Reject(() => new BookingQueueStore(queryPath)), "oversized queue file rejected before parsing");
        File.WriteAllText(queryPath, before, Encoding.UTF8); File.Delete(queryPath);
        Check(Reject(() => new BookingQueueStore(queryPath)) && Reject(() => queryStore.Add(new[] { Target(705) }, Options())), "missing initialized queue fails closed both live and after restart");
    }

    static void RunAllCandidates(string directory)
    {
        string path = Path.Combine(directory, "all-candidates.json");
        var store = new BookingQueueStore(path);
        var attempts = new BookingAttemptStore(Path.Combine(directory, "all-attempts.json"));
        var earlier = store.Add(new[] { Target(801) }, Options());
        var all = store.Add(new[] { Target(811), Target(813), Target(815) }, new RefreshOptions(5, 3, 10, true, BookingCompletionMode.All));
        Check(all.Unlimited && all.CompletionMode == BookingCompletionMode.All && all.RefreshCount == 0 && all.ConfirmedCount == 0, "All job preserves explicit completion policy and unlimited flag");
        Check(Reject(() => store.Begin(all.Id)), "ordinary queue start still cannot skip an earlier saved journey");
        Check(store.BeginSelected(all.Id).Id == all.Id && store.Get(earlier.Id).State == BookingQueueState.Pending, "explicit current selection starts only its own batch and preserves earlier saved order");
        store.RecordRefreshCount(all.Id, 7); store.RecordRefreshCount(all.Id, 7);
        Check(store.Get(all.Id).RefreshCount == 7 && Reject(() => store.RecordRefreshCount(all.Id, 6)) && Reject(() => store.RecordRefreshCount(all.Id, -1)), "cumulative refresh count is durable and cannot decrease");
        var first = attempts.Begin(all.Targets[1]); store.BindAttempt(all.Id, first);
        Check(Reject(() => store.Complete(all.Id, first)) && store.Get(all.Id).ConfirmedCount == 0, "All never counts a prepared click as a successful reservation");
        var confirmedFirst = Confirm(attempts, first);
        Check(store.Complete(all.Id, confirmedFirst), "first confirmed All candidate records progress");
        var partial = store.Get(all.Id);
        Check(partial.State == BookingQueueState.Pending && partial.ConfirmedCount == 1 && partial.CompletedUtc == null && partial.AttemptId == null,
            "partial All completion returns the same job to pending without declaring the whole job completed");
        Check(partial.RemainingTargets().Select(t => t.Key).SequenceEqual(new[] { all.Targets[0].Key, all.Targets[2].Key }) && partial.RefreshCount == 7,
            "remaining All candidates exclude the confirmed train and retain the accumulated query budget");
        partial.Completions[0].TargetKey = "altered"; partial.Completions.Clear(); partial.RemainingTargets().Clear();
        Check(store.Get(all.Id).ConfirmedCount == 1 && store.Get(all.Id).RemainingTargets().Count == 2, "returned completion progress and remaining selections cannot mutate persistence");
        Check(!store.Complete(all.Id, confirmedFirst) && store.Get(all.Id).ConfirmedCount == 1, "duplicate first receipt is idempotent while later candidates remain");
        store = new BookingQueueStore(path);
        Check(store.Get(all.Id).State == BookingQueueState.Pending && store.Get(all.Id).ConfirmedCount == 1 && store.Get(all.Id).RefreshCount == 7,
            "restart after confirmed All candidate preserves safe remaining work and count");
        store.BeginSelected(all.Id);
        Check(Reject(() => store.BindAttempt(all.Id, first)), "already confirmed candidate cannot bind as the next All attempt");
        var second = attempts.Begin(all.Targets[0]); store.BindAttempt(all.Id, second);
        Check(!store.Complete(all.Id, confirmedFirst) && store.Get(all.Id).AttemptId == second.Id, "late duplicate receipt cannot replace the next active attempt");
        attempts.MarkSubmitted(second.Id); attempts.RecordUnknown(second.Id, "synthetic uncertain result");
        Check(Reject(() => store.Complete(all.Id, attempts.Get(second.Id))), "uncertain second reservation never advances All progress");
        store = new BookingQueueStore(path);
        Check(store.Get(all.Id).State == BookingQueueState.NeedsReview && store.Get(all.Id).ConfirmedCount == 1 && store.Get(all.Id).AttemptId == second.Id,
            "restart with an uncertain All attempt freezes that attempt and preserves earlier confirmation");
        Check(Reject(() => store.BeginSelected(all.Id)) && Reject(() => store.BeginNext()), "uncertain All attempt blocks automatic retry and other queued journeys");
        attempts.RecordConfirmed(second.Id, BookingReceipt.CreateConfirmed(second.Target, second.Target, "2345-6789", null));
        Check(store.Complete(all.Id, attempts.Get(second.Id)) && store.Get(all.Id).ConfirmedCount == 2 && store.Get(all.Id).State == BookingQueueState.Pending,
            "verified review result advances All without repeating the uncertain attempt");
        store.BeginSelected(all.Id); store.RecordRefreshCount(all.Id, (long)Int32.MaxValue + 8);
        var third = attempts.Begin(all.Targets[2]); store.BindAttempt(all.Id, third);
        var confirmedThird = Confirm(attempts, third);
        Check(store.Complete(all.Id, confirmedThird) && store.Get(all.Id).State == BookingQueueState.Completed && store.Get(all.Id).ConfirmedCount == 3,
            "All finishes only after each distinct selected train has a confirmed receipt");
        Check(store.Get(all.Id).RemainingTargets().Count == 0 && store.Get(all.Id).RefreshCount == (long)Int32.MaxValue + 8 && !store.Complete(all.Id, confirmedThird),
            "completed All has no remaining candidates, preserves long counter and ignores duplicate final receipt");
        Check(store.Get(earlier.Id).State == BookingQueueState.Pending && store.GetAll().Select(j => j.Id).SequenceEqual(new[] { earlier.Id, all.Id }),
            "isolated selected batch leaves existing jobs and their order untouched");
        Check(new BookingQueueStore(path).Get(all.Id).ConfirmedCount == 3, "every All confirmation survives a completed restart");

        var reset = store.Add(new[] { Target(821), Target(823) }, new RefreshOptions(5, 10, 0, false, BookingCompletionMode.All));
        store.BeginSelected(reset.Id);
        var resetFirst = attempts.Begin(reset.Targets[0]); store.BindAttempt(reset.Id, resetFirst); store.Complete(reset.Id, Confirm(attempts, resetFirst));
        store.BeginSelected(reset.Id); store.MarkNeedsReview(reset.Id); store.ResetAfterUserConfirmed(reset.Id, true);
        Check(store.Get(reset.Id).ConfirmedCount == 1 && store.Get(reset.Id).RemainingTargets().Single().Key == reset.Targets[1].Key,
            "manual review reset preserves confirmed All candidates instead of booking them again");
        Check(Reject(() => store.RecordRefreshCount(reset.Id, 1)), "inactive job cannot receive automatic query count updates");

        string clean = File.ReadAllText(path);
        var serializer = new JavaScriptSerializer();
        var payload = serializer.Deserialize<BookingQueueStore.StoredQueue>(clean);
        payload.Jobs.Single(j => j.Id == all.Id).Completions[1].TargetKey = payload.Jobs.Single(j => j.Id == all.Id).Completions[0].TargetKey;
        File.WriteAllText(path, serializer.Serialize(payload));
        Check(Reject(() => new BookingQueueStore(path)), "duplicate persisted completed target is rejected before any restart");
        File.WriteAllText(path, clean);
        payload = serializer.Deserialize<BookingQueueStore.StoredQueue>(clean); payload.Jobs[0].CompletionMode = (BookingCompletionMode)99;
        File.WriteAllText(path, serializer.Serialize(payload));
        Check(Reject(() => new BookingQueueStore(path)), "unknown persisted completion policy fails closed");
        File.WriteAllText(path, clean);
    }
}
