using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal sealed class BookingTarget
{
    public string Date { get; set; }
    public string DepartureStation { get; set; }
    public string ArrivalStation { get; set; }
    public string Train { get; set; }
    public string Departure { get; set; }
    public string Arrival { get; set; }
    public string Grade { get; set; }
    public int Passengers { get; set; }

    public BookingTarget() { Passengers = 1; }
    public BookingTarget Copy() { return (BookingTarget)MemberwiseClone(); }
    public string Validate()
    {
        DateTime date;
        if (!DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return "승차 날짜를 확인하세요.";
        if (!Text(DepartureStation, 40) || !Text(ArrivalStation, 40) || DepartureStation.Trim() == ArrivalStation.Trim()) return "서로 다른 출발역과 도착역을 지정하세요.";
        if (!Text(Train, 80) || !Regex.IsMatch(Train, @"\d{1,5}\s*$") || Regex.IsMatch(Train, @"\d{6,}\s*$")) return "열차 번호가 포함된 열차명을 지정하세요.";
        if (!Clock(Departure) || !Clock(Arrival)) return "열차의 출발 및 도착 시각을 확인하세요.";
        if (Grade != "일반실" && Grade != "특실" && Grade != "우등실") return "좌석 등급을 확인하세요.";
        if (Passengers != 1) return "이 도우미는 1명 예약만 지원합니다.";
        return null;
    }
    static bool Text(string value, int max) { return !String.IsNullOrWhiteSpace(value) && value.Trim().Length <= max && !value.Any(Char.IsControl); }
    static bool Clock(string value) { return value != null && Regex.IsMatch(value, @"^(?:[01]\d|2[0-3]):[0-5]\d$"); }
    [ScriptIgnore]
    public string Key
    {
        get
        {
            string invalid = Validate();
            if (invalid != null) throw new ArgumentException(invalid);
            // Train display spacing and leading zeros do not create another reservation identity.
            string number = Int32.Parse(Regex.Match(Train, @"(\d{1,5})\s*$").Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            string identity = new JavaScriptSerializer().Serialize(new string[] { Date, DepartureStation.Trim(), ArrivalStation.Trim(), number, Departure, Arrival, Grade });
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }
    }
}

internal sealed class BookingReceipt
{
    public string TargetKey { get; set; }
    public string ReservationNumber { get; set; }
    public string PaymentDeadline { get; set; }
    public string EvidenceType { get; set; }
    public string Seat { get; set; }

    // The caller must first establish an explicit official success state. This factory
    // checks identity, not site-specific success text or the existence of a reservation.
    public static BookingReceipt CreateConfirmed(BookingTarget expected, BookingTarget observed, string reservationNumber, string paymentDeadline)
    {
        return CreateConfirmed(expected, observed, reservationNumber, paymentDeadline, null, null);
    }
    public static BookingReceipt CreateConfirmed(BookingTarget expected, BookingTarget observed, string reservationNumber, string paymentDeadline, string evidenceType, string seat)
    {
        if (expected == null || observed == null || expected.Validate() != null || observed.Validate() != null || expected.Key != observed.Key)
            throw new ArgumentException("예약 결과가 선택한 날짜·열차·구간·등급과 일치하지 않습니다.");
        var result = new BookingReceipt { TargetKey = expected.Key, ReservationNumber = String.IsNullOrWhiteSpace(reservationNumber) ? null : reservationNumber.Trim(), PaymentDeadline = String.IsNullOrWhiteSpace(paymentDeadline) ? null : paymentDeadline.Trim(), EvidenceType = evidenceType, Seat = seat };
        string invalid = result.ValidateForConfirmation(expected);
        if (invalid != null) throw new ArgumentException(invalid);
        return result;
    }
    public BookingReceipt Copy() { return (BookingReceipt)MemberwiseClone(); }
    internal string Validate(BookingTarget target)
    {
        if (target == null || target.Validate() != null || TargetKey != target.Key) return "예약 대상이 일치하지 않습니다.";
        if (EvidenceType != null && EvidenceType != "reservation-number" && EvidenceType != "seat-detail") return "예약 확인 근거가 올바르지 않습니다.";
        bool hasNumber = !String.IsNullOrWhiteSpace(ReservationNumber);
        if ((EvidenceType != "seat-detail" && !hasNumber) || (hasNumber && (ReservationNumber.Length > 80 || ReservationNumber.Any(Char.IsControl) || !Regex.IsMatch(ReservationNumber, @"^[A-Za-z0-9][A-Za-z0-9 -]*$")))) return "예약 번호 형식이 올바르지 않습니다.";
        if (PaymentDeadline != null && (PaymentDeadline.Length > 160 || PaymentDeadline.Any(Char.IsControl))) return "결제 기한 형식이 올바르지 않습니다.";
        if (EvidenceType == "seat-detail")
        {
            DateTimeOffset deadline;
            if (Seat == null || !Regex.IsMatch(Seat, @"\A[1-9][0-9]{0,2}호차 [1-9][0-9]{0,2}[A-F]\z")) return "한 명의 호차·좌석을 명확히 확인할 수 없습니다.";
            if (!TryPaymentDeadline(out deadline)) return "결제 기한의 날짜와 시각을 확인할 수 없습니다.";
        }
        else if (Seat != null) return "좌석 확인 근거가 올바르지 않습니다.";
        return null;
    }
    bool TryPaymentDeadline(out DateTimeOffset deadline)
    {
        DateTime local;
        deadline = default(DateTimeOffset);
        if (!DateTime.TryParseExact(PaymentDeadline, "yyyy. MM. dd. HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) return false;
        deadline = new DateTimeOffset(local, TimeSpan.FromHours(9));
        return true;
    }
    internal string ValidateForConfirmation(BookingTarget target)
    {
        string invalid = Validate(target); if (invalid != null) return invalid;
        if (EvidenceType == "seat-detail")
        {
            DateTimeOffset deadline;
            if (!TryPaymentDeadline(out deadline) || deadline <= DateTimeOffset.UtcNow) return "이미 지난 결제 기한으로 예약 완료를 확인할 수 없습니다.";
        }
        return null;
    }
    public string BuildTelegramMessage(BookingTarget target)
    {
        string invalid = Validate(target); if (invalid != null) throw new ArgumentException(invalid);
        return "코레일 도우미 · 예약 완료 · 미결제\n" + target.Date + " · " + target.DepartureStation.Trim() + " → " + target.ArrivalStation.Trim()
            + "\n" + target.Train.Trim() + " · " + target.Departure + " → " + target.Arrival + " · " + target.Grade + " · 1명"
            + (Seat == null ? "" : "\n좌석: " + Seat)
            + (String.IsNullOrWhiteSpace(ReservationNumber) ? "" : "\n예약 번호: " + ReservationNumber)
            + "\n결제 기한: " + (String.IsNullOrWhiteSpace(PaymentDeadline) ? "공식 예약 화면에서 확인하세요." : PaymentDeadline)
            + "\n결제는 완료되지 않았습니다. 코레일 공식 화면에서 직접 결제하세요.";
    }
}

internal enum BookingAttemptState { Prepared, Submitted, Unknown, Confirmed, Released }
internal enum BookingNotificationState { None, Pending, SendingOrUnknown, Sent, Failed }

internal sealed class BookingAttempt
{
    public string Id { get; set; }
    public BookingTarget Target { get; set; }
    public BookingAttemptState State { get; set; }
    public string CreatedUtc { get; set; }
    public string SubmittedUtc { get; set; }
    public string ConfirmedUtc { get; set; }
    public BookingReceipt Receipt { get; set; }
    public BookingNotificationState NotificationStatus { get; set; }
    public string NotificationError { get; set; }
    [ScriptIgnore]
    public bool PendingNotification { get { return State == BookingAttemptState.Confirmed && NotificationStatus == BookingNotificationState.Pending; } }
    [ScriptIgnore]
    public bool IsLocked { get { return State != BookingAttemptState.Released; } }
    public BookingAttempt Copy()
    {
        var result = (BookingAttempt)MemberwiseClone();
        result.Target = Target == null ? null : Target.Copy(); result.Receipt = Receipt == null ? null : Receipt.Copy();
        return result;
    }
}

internal sealed class BookingAttemptStore
{
    public sealed class StoredAttempts
    {
        public int Version { get; set; }
        public List<BookingAttempt> Attempts { get; set; }
    }
    const int MaximumBytes = 2097152;
    const int MaximumAttempts = 2000;
    readonly string path;
    bool sawStore;
    public BookingAttemptStore(string filePath)
    {
        if (String.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("예약 기록 경로가 필요합니다.");
        path = Path.GetFullPath(filePath);
        // Invalid data never falls back to an empty store.
        Read();
    }
    static string Timestamp() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    static bool ValidTimestamp(string value)
    { DateTime result; return value != null && DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result); }
    StoredAttempts Read()
    {
        if (!File.Exists(path))
        {
            if (sawStore || File.Exists(path + ".initialized")) throw new InvalidDataException("예약 기록이 없어졌습니다. 공식 예약 내역을 확인하기 전에는 재예약할 수 없습니다.");
            return new StoredAttempts { Version = 1, Attempts = new List<BookingAttempt>() };
        }
        sawStore = true;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException();
            var data = new JavaScriptSerializer { MaxJsonLength = MaximumBytes }.Deserialize<StoredAttempts>(File.ReadAllText(path, Encoding.UTF8));
            if (data == null || data.Version != 1 || data.Attempts == null || data.Attempts.Count > MaximumAttempts) throw new InvalidDataException();
            var ids = new HashSet<string>(); var locked = new HashSet<string>();
            foreach (var attempt in data.Attempts)
            {
                if (attempt == null || attempt.Id == null || !Regex.IsMatch(attempt.Id, @"^[a-f0-9]{32}$") || !ids.Add(attempt.Id)
                    || attempt.Target == null || attempt.Target.Validate() != null || !Enum.IsDefined(typeof(BookingAttemptState), attempt.State)
                    || !Enum.IsDefined(typeof(BookingNotificationState), attempt.NotificationStatus) || !ValidTimestamp(attempt.CreatedUtc)
                    || (attempt.SubmittedUtc != null && !ValidTimestamp(attempt.SubmittedUtc)) || (attempt.ConfirmedUtc != null && !ValidTimestamp(attempt.ConfirmedUtc))) throw new InvalidDataException();
                if (attempt.IsLocked && !locked.Add(attempt.Target.Key)) throw new InvalidDataException();
                if ((attempt.State == BookingAttemptState.Submitted || attempt.State == BookingAttemptState.Confirmed) && attempt.SubmittedUtc == null) throw new InvalidDataException();
                if (attempt.State == BookingAttemptState.Confirmed && (attempt.Receipt == null || attempt.ConfirmedUtc == null)) throw new InvalidDataException();
                if (attempt.Receipt != null && (attempt.Receipt.Validate(attempt.Target) != null || attempt.ConfirmedUtc == null || attempt.SubmittedUtc == null)) throw new InvalidDataException();
                if ((attempt.Receipt == null && attempt.NotificationStatus != BookingNotificationState.None)
                    || (attempt.Receipt != null && attempt.NotificationStatus == BookingNotificationState.None)) throw new InvalidDataException();
                if (attempt.NotificationError != null && (attempt.NotificationError.Length > 200 || attempt.NotificationError.Any(Char.IsControl))) throw new InvalidDataException();
            }
            return data;
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is IOException) throw;
            throw new InvalidDataException("예약 기록을 읽을 수 없습니다. 공식 예약 내역을 확인하기 전에는 재예약할 수 없습니다.");
        }
    }
    void Write(StoredAttempts data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = MaximumBytes }.Serialize(data));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("예약 기록이 너무 큽니다.");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            sawStore = true;
            // A missing store after first use must not silently permit another submission.
            using (var marker = new FileStream(path + ".initialized", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) marker.Flush(true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    T Change<T>(Func<StoredAttempts, T> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var data = Read(); T result = change(data); Write(data); return result;
        }
    }
    static BookingAttempt Require(StoredAttempts data, string id)
    {
        var result = data.Attempts.FirstOrDefault(a => a.Id == id);
        if (result == null) throw new InvalidOperationException("예약 시도 기록을 찾을 수 없습니다.");
        return result;
    }
    public BookingAttempt Begin(BookingTarget target)
    {
        if (target == null || target.Validate() != null) throw new ArgumentException(target == null ? "예약 대상이 필요합니다." : target.Validate());
        var snapshot = target.Copy();
        return Change(data => {
            if (data.Attempts.Any(a => a.IsLocked && a.Target.Key == snapshot.Key)) throw new InvalidOperationException("이 열차는 이미 예약을 시도했습니다. 공식 예약 내역을 확인한 뒤에만 수동으로 잠금을 해제하세요.");
            if (data.Attempts.Count >= MaximumAttempts) throw new InvalidOperationException("예약 기록 한도에 도달했습니다.");
            var attempt = new BookingAttempt { Id = Guid.NewGuid().ToString("N"), Target = snapshot, State = BookingAttemptState.Prepared, CreatedUtc = Timestamp(), NotificationStatus = BookingNotificationState.None };
            data.Attempts.Add(attempt); return attempt.Copy();
        });
    }
    public void MarkSubmitted(string id)
    {
        Change(data => {
            var attempt = Require(data, id);
            if (attempt.State != BookingAttemptState.Prepared) throw new InvalidOperationException("이미 제출했거나 종료된 예약은 다시 제출할 수 없습니다.");
            attempt.State = BookingAttemptState.Submitted; attempt.SubmittedUtc = Timestamp(); return true;
        });
    }
    public void RecordUnknown(string id, string reason)
    {
        Change(data => {
            var attempt = Require(data, id);
            if (attempt.State != BookingAttemptState.Prepared && attempt.State != BookingAttemptState.Submitted && attempt.State != BookingAttemptState.Unknown)
                throw new InvalidOperationException("확인되었거나 해제된 예약 상태를 변경할 수 없습니다.");
            // Raw site / network error strings can contain credentials or private data.
            attempt.State = BookingAttemptState.Unknown; return true;
        });
    }
    public void RecordConfirmed(string id, BookingReceipt receipt)
    {
        Change(data => {
            var attempt = Require(data, id);
            if (receipt == null || receipt.Validate(attempt.Target) != null) throw new ArgumentException("선택한 열차와 일치하는 예약 결과가 필요합니다.");
            if (attempt.State == BookingAttemptState.Confirmed)
            {
                if (attempt.Receipt.ReservationNumber != receipt.ReservationNumber) throw new InvalidOperationException("이미 확인한 예약과 다른 예약 번호입니다.");
                if ((attempt.Receipt.EvidenceType == "seat-detail" || receipt.EvidenceType == "seat-detail")
                    && (attempt.Receipt.EvidenceType != receipt.EvidenceType || attempt.Receipt.Seat != receipt.Seat || attempt.Receipt.PaymentDeadline != receipt.PaymentDeadline))
                    throw new InvalidOperationException("이미 확인한 예약과 다른 좌석 또는 결제 기한입니다.");
                return true;
            }
            if (receipt.ValidateForConfirmation(attempt.Target) != null) throw new ArgumentException("유효한 결제 기한의 예약 결과가 필요합니다.");
            if ((attempt.State != BookingAttemptState.Submitted && attempt.State != BookingAttemptState.Unknown) || attempt.SubmittedUtc == null)
                throw new InvalidOperationException("제출한 예약 시도만 완료로 확인할 수 있습니다.");
            attempt.State = BookingAttemptState.Confirmed; attempt.Receipt = receipt.Copy(); attempt.ConfirmedUtc = Timestamp(); attempt.NotificationStatus = BookingNotificationState.Pending; return true;
        });
    }
    public bool BeginNotification(string id)
    {
        return Change(data => {
            var attempt = Require(data, id); if (!attempt.PendingNotification) return false;
            // Persist before HTTP. A crash or timeout cannot trigger automatic retransmission.
            attempt.NotificationStatus = BookingNotificationState.SendingOrUnknown; return true;
        });
    }
    public void CompleteNotification(string id, bool success, string error)
    {
        Change(data => {
            var attempt = Require(data, id);
            if (attempt.State != BookingAttemptState.Confirmed || attempt.NotificationStatus != BookingNotificationState.SendingOrUnknown)
                throw new InvalidOperationException("전송 중인 예약 알림이 없습니다.");
            attempt.NotificationStatus = success ? BookingNotificationState.Sent : BookingNotificationState.Failed;
            attempt.NotificationError = success ? null : "텔레그램 전송에 실패했거나 도착 여부를 확인할 수 없습니다. 공식 예약 내역을 확인하세요.";
            return true;
        });
    }
    public BookingAttempt Get(string id) { var result = Read().Attempts.FirstOrDefault(a => a.Id == id); return result == null ? null : result.Copy(); }
    public BookingAttempt FindByTarget(BookingTarget target)
    {
        if (target == null || target.Validate() != null) throw new ArgumentException("예약 대상이 올바르지 않습니다.");
        var result = Read().Attempts.FirstOrDefault(a => a.IsLocked && a.Target.Key == target.Key); return result == null ? null : result.Copy();
    }
    public IList<BookingAttempt> GetAll() { return Read().Attempts.OrderByDescending(a => a.CreatedUtc, StringComparer.Ordinal).Select(a => a.Copy()).ToList(); }
    // Call only from an explicit user action after the user checks the official result.
    public void ReleaseAfterUserConfirmed(string id, bool userConfirmed)
    {
        if (!userConfirmed) throw new InvalidOperationException("공식 예약 결과를 확인했다는 사용자 확인이 필요합니다.");
        Change(data => { Require(data, id).State = BookingAttemptState.Released; return true; });
    }
}
