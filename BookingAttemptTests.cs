using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Pure local persistence/state tests. No browser, personal profile, or network.
internal static class BookingAttemptTests
{
    static int count;
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } catch (InvalidDataException) { return true; } catch (IOException) { return true; } catch (UnauthorizedAccessException) { return true; } }
    static BookingTarget Target()
    {
        return new BookingTarget { Date = "2026-10-25", DepartureStation = "서울", ArrivalStation = "부산", Train = "KTX 101", Departure = "07:30", Arrival = "10:15", Grade = "일반실", Passengers = 1 };
    }
    static int Main(string[] args)
    {
        try
        {
            string parent = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "korail-booking-tests") : args[0];
            string directory = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            Run(directory); Console.WriteLine("PASS " + count + " booking state checks (local files only)"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run(string directory)
    {
        var target = Target(); Check(target.Validate() == null, "valid single passenger target");
        var alternate = target.Copy(); alternate.Train = "KTX00101";
        Check(alternate.Key == target.Key, "train whitespace and leading zeros do not bypass duplicate guard");
        alternate = target.Copy(); alternate.Date = "2026-10-26"; Check(alternate.Key != target.Key, "different date has separate key");
        alternate = target.Copy(); alternate.Grade = "특실"; Check(alternate.Key != target.Key, "different grade has separate key");
        alternate = target.Copy(); alternate.Passengers = 2; Check(alternate.Validate() != null, "multiple passengers rejected");
        alternate = target.Copy(); alternate.Train = "KTX"; Check(alternate.Validate() != null, "train without number rejected");
        alternate = target.Copy(); alternate.Arrival = "24:15"; Check(alternate.Validate() != null, "invalid train clock rejected");
        string path = Path.Combine(directory, "attempts.json"); var store = new BookingAttemptStore(path);
        Check(store.GetAll().Count == 0 && !File.Exists(path), "empty inspection does not reserve or create records");
        var attempt = store.Begin(target);
        Check(File.Exists(path) && store.Get(attempt.Id).State == BookingAttemptState.Prepared, "begin persists before submission");
        target.Train = "KTX 999"; attempt.Target.Train = "KTX 888";
        Check(store.Get(attempt.Id).Target.Train == "KTX 101", "input and returned record cannot mutate persisted target");
        target = Target();
        Check(Reject(() => store.Begin(target)), "prepared attempt blocks duplicate submission");
        store = new BookingAttemptStore(path); Check(Reject(() => store.Begin(target)), "restart retains prepared lock");
        store.MarkSubmitted(attempt.Id); Check(store.Get(attempt.Id).State == BookingAttemptState.Submitted, "submitted state persisted before actual click");
        Check(Reject(() => store.MarkSubmitted(attempt.Id)), "submitted attempt cannot be submitted again");
        store.RecordUnknown(attempt.Id, "SYNTHETIC_PRIVATE_ERROR");
        Check(store.Get(attempt.Id).State == BookingAttemptState.Unknown && Reject(() => store.Begin(target)), "unknown result retains booking lock");
        Check(!File.ReadAllText(path).Contains("SYNTHETIC_PRIVATE_ERROR"), "raw errors are not persisted");
        var observed = target.Copy(); observed.Date = "2026-10-26";
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, observed, "1234-5678", null)), "wrong result date rejected");
        observed = target.Copy(); observed.Grade = "특실";
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, observed, "1234-5678", null)), "wrong result grade rejected");
        observed = target.Copy(); observed.Train = "KTX 102";
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, observed, "1234-5678", null)), "wrong result train rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, "1234\nFORGED", null)), "newline in receipt rejected");
        var receipt = BookingReceipt.CreateConfirmed(target, target, "1234-5678", "2026-10-24 23:59");
        store.RecordConfirmed(attempt.Id, receipt);
        Check(store.Get(attempt.Id).PendingNotification && store.Get(attempt.Id).State == BookingAttemptState.Confirmed, "confirmation enables notification only after submission");
        string message = receipt.BuildTelegramMessage(target);
        Check(message.Contains("예약 완료 · 미결제") && message.Contains("1234-5678") && message.Contains("2026-10-24 23:59") && message.Contains("서울 → 부산") && message.Contains("일반실"), "message includes confirmed journey receipt and observed deadline");
        var withoutDeadline = BookingReceipt.CreateConfirmed(target, target, "1234-5678", null);
        Check(withoutDeadline.BuildTelegramMessage(target).Contains("공식 예약 화면에서 확인하세요."), "unknown deadline is not invented");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, null)), "legacy receipt still requires reservation number");
        string futureDeadline = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).AddHours(2).ToString("yyyy. MM. dd. HH:mm", CultureInfo.InvariantCulture);
        string expiredDeadline = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).AddHours(-2).ToString("yyyy. MM. dd. HH:mm", CultureInfo.InvariantCulture);
        var seatReceipt = BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "seat-detail", "9호차 12A");
        string seatMessage = seatReceipt.BuildTelegramMessage(target);
        Check(seatReceipt.ReservationNumber == null && seatMessage.Contains("예약 완료 · 미결제") && seatMessage.Contains("좌석: 9호차 12A") && seatMessage.Contains(futureDeadline) && !seatMessage.Contains("예약 번호:"), "confirmed seat detail does not invent reservation number");
        Check(BookingReceipt.CreateConfirmed(target, target, "1234-5678", futureDeadline, "seat-detail", "9호차 12A").BuildTelegramMessage(target).Contains("예약 번호: 1234-5678"), "seat detail includes observed reservation number only when supplied");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "seat-detail", null)), "seat detail without seat rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "seat-detail", "9호차 12A, 12B")), "multiple seats rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "seat-detail", "9호차 12A\n")), "seat trailing newline rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "seat-detail", "9호차 0A")), "zero seat rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, null, "seat-detail", "9호차 12A")), "seat detail without deadline rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, "22:17", "seat-detail", "9호차 12A")), "deadline without explicit date rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, "2026. 02. 30. 22:17", "seat-detail", "9호차 12A")), "invalid calendar deadline rejected");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, expiredDeadline, "seat-detail", "9호차 12A")), "expired seat detail cannot create confirmation");
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "unknown", "9호차 12A")), "unknown evidence type rejected");
        observed = target.Copy(); observed.Train = "KTX 102";
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, observed, null, futureDeadline, "seat-detail", "9호차 12A")), "seat detail wrong target rejected");
        observed = target.Copy(); observed.Passengers = 2;
        Check(Reject(() => BookingReceipt.CreateConfirmed(target, observed, null, futureDeadline, "seat-detail", "9호차 12A")), "seat detail multiple passengers rejected");
        var seatStore = new BookingAttemptStore(Path.Combine(directory, "seat-attempts.json"));
        var seatAttempt = seatStore.Begin(target); seatStore.MarkSubmitted(seatAttempt.Id);
        var staleReceipt = seatReceipt.Copy(); staleReceipt.PaymentDeadline = expiredDeadline;
        Check(Reject(() => seatStore.RecordConfirmed(seatAttempt.Id, staleReceipt)), "first durable confirmation rechecks deadline even for manually constructed receipt");
        seatStore.RecordConfirmed(seatAttempt.Id, seatReceipt);
        Check(seatStore.Get(seatAttempt.Id).PendingNotification && seatStore.Get(seatAttempt.Id).Receipt.Seat == "9호차 12A", "seat evidence persists and enables notification after submission");
        var otherSeat = BookingReceipt.CreateConfirmed(target, target, null, futureDeadline, "seat-detail", "9호차 12B");
        Check(Reject(() => seatStore.RecordConfirmed(seatAttempt.Id, otherSeat)), "different seat without reservation number cannot replace confirmed receipt");
        string seatPath = Path.Combine(directory, "seat-attempts.json");
        File.WriteAllText(seatPath, File.ReadAllText(seatPath).Replace(futureDeadline, expiredDeadline), Encoding.UTF8);
        seatStore = new BookingAttemptStore(seatPath);
        Check(seatStore.Get(seatAttempt.Id).Receipt.PaymentDeadline == expiredDeadline && seatStore.FindByTarget(target).IsLocked, "expired historical seat receipt remains readable and locked");
        seatStore.RecordConfirmed(seatAttempt.Id, seatStore.Get(seatAttempt.Id).Receipt);
        Check(seatStore.BeginNotification(seatAttempt.Id) && !seatStore.BeginNotification(seatAttempt.Id), "expired historical receipt preserves notification deduplication");
        Check(store.BeginNotification(attempt.Id) && !store.BeginNotification(attempt.Id), "network handoff is consumed before HTTP");
        store = new BookingAttemptStore(path);
        Check(!store.BeginNotification(attempt.Id) && store.Get(attempt.Id).NotificationStatus == BookingNotificationState.SendingOrUnknown, "restart cannot retransmit uncertain notification automatically");
        store.CompleteNotification(attempt.Id, false, "SYNTHETIC_BOT_TOKEN_SECRET");
        Check(store.Get(attempt.Id).NotificationStatus == BookingNotificationState.Failed && Reject(() => store.Begin(target)), "Telegram failure never releases booking lock");
        Check(!File.ReadAllText(path).Contains("SYNTHETIC_BOT_TOKEN_SECRET"), "raw notification errors are not persisted");
        store.RecordConfirmed(attempt.Id, receipt); Check(!store.BeginNotification(attempt.Id), "duplicate result does not reset failed notification");
        Check(Reject(() => store.RecordUnknown(attempt.Id, "late result")), "confirmed reservation cannot be downgraded to unknown");
        Check(Reject(() => store.ReleaseAfterUserConfirmed(attempt.Id, false)) && store.FindByTarget(target) != null, "missing manual confirmation cannot release lock");
        store.ReleaseAfterUserConfirmed(attempt.Id, true);
        Check(store.FindByTarget(target) == null && store.Get(attempt.Id).State == BookingAttemptState.Released, "explicit user confirmation releases target only");
        var second = store.Begin(target); Check(second.Id != attempt.Id, "manual release allows a new uniquely identified attempt");
        Check(Reject(() => store.RecordConfirmed(second.Id, receipt)), "preparation without submission cannot report success");
        var later = target.Copy(); later.Date = "2026-10-26"; Check(store.Begin(later).Id != second.Id, "another date may be submitted independently");
        var entries = store.GetAll(); entries[0].Target.Train = "changed";
        Check(store.GetAll().All(a => a.Target.Train != "changed"), "history returns detached copies");

        string before = File.ReadAllText(path);
        using (var locked = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Reject(() => store.MarkSubmitted(second.Id)), "failed persistence prevents successful submission transition");
        Check(File.ReadAllText(path) == before && store.Get(second.Id).State == BookingAttemptState.Prepared, "failed mutation preserves prior durable state");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic writes leave no temporary files");
        store.MarkSubmitted(second.Id); store.RecordConfirmed(second.Id, receipt);
        Check(store.BeginNotification(second.Id), "new confirmed attempt gets one notification");
        store.CompleteNotification(second.Id, true, null);
        Check(store.Get(second.Id).NotificationStatus == BookingNotificationState.Sent && !store.BeginNotification(second.Id), "successful notification is durable and deduplicated");

        string invalidPath = Path.Combine(directory, "invalid.json"); File.WriteAllText(invalidPath, "{truncated", Encoding.UTF8);
        Check(Reject(() => new BookingAttemptStore(invalidPath)), "corrupt store fails closed");
        File.WriteAllText(invalidPath, "{\"Version\":999,\"Attempts\":[]}", Encoding.UTF8);
        Check(Reject(() => new BookingAttemptStore(invalidPath)), "unknown store version fails closed");
        var stale = new BookingAttemptStore(path); File.WriteAllText(path, "{invalid", Encoding.UTF8);
        Check(Reject(() => stale.Begin(Target())), "corruption after startup cannot bypass lock");
        Check(File.ReadAllText(path) == "{invalid", "invalid store is not overwritten with empty data");
        File.WriteAllText(path, before, Encoding.UTF8);
        File.Delete(path);
        Check(Reject(() => new BookingAttemptStore(path)), "missing previously initialized store fails closed after restart");
    }
}
