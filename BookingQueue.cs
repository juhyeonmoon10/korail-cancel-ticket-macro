using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal enum BookingQueueState { Pending, Running, Completed, NeedsReview }

internal sealed class BookingQueueCompletion
{
    public string AttemptId { get; set; }
    public string TargetKey { get; set; }
    public string ConfirmedUtc { get; set; }
    public BookingQueueCompletion Copy() { return (BookingQueueCompletion)MemberwiseClone(); }
}

// Confirmed candidates remain durable when All continues the same job. They
// are excluded from every subsequent selection, including after a restart.
internal sealed class BookingQueueJob
{
    public string Id { get; set; }
    public List<BookingTarget> Targets { get; set; }
    public int IntervalSeconds { get; set; }
    public int Limit { get; set; }
    public int JitterPercent { get; set; }
    public bool Unlimited { get; set; }
    public BookingCompletionMode CompletionMode { get; set; }
    public long RefreshCount { get; set; }
    public List<BookingQueueCompletion> Completions { get; set; }
    public string QueryJson { get; set; }
    public BookingQueueState State { get; set; }
    public string CreatedUtc { get; set; }
    public string StartedUtc { get; set; }
    public string CompletedUtc { get; set; }
    public string AttemptId { get; set; }
    public string AttemptTargetKey { get; set; }
    public string CompletedTargetKey { get; set; }

    public BookingQueueJob Copy()
    {
        var result = (BookingQueueJob)MemberwiseClone();
        result.Targets = Targets == null ? null : Targets.Select(t => t.Copy()).ToList();
        result.Completions = Completions == null ? new List<BookingQueueCompletion>() : Completions.Select(c => c.Copy()).ToList();
        return result;
    }
    public List<BookingTarget> RemainingTargets()
    {
        var completed = new HashSet<string>((Completions ?? new List<BookingQueueCompletion>()).Select(c => c.TargetKey), StringComparer.Ordinal);
        return Targets.Where(t => !completed.Contains(t.Key)).Select(t => t.Copy()).ToList();
    }
    public int ConfirmedCount { get { return Completions == null ? 0 : Completions.Count; } }
}

internal sealed class BookingQueueStore
{
    public sealed class StoredQueue
    {
        public int Version { get; set; }
        public List<BookingQueueJob> Jobs { get; set; }
    }
    const int MaximumBytes = 1048576;
    public const int MaximumJobs = 20;
    public const int MaximumTargets = 20;
    readonly string path;
    bool sawStore;

    public BookingQueueStore(string filePath)
    {
        if (String.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("예약 순서 파일 경로가 필요합니다.");
        path = Path.GetFullPath(filePath);
        // A restarted process cannot know whether the last mouse click reached
        // Korail. Preserve the job and its attempt binding for explicit review.
        var data = Read();
        if (data.Jobs.Any(j => j.State == BookingQueueState.Running))
            Change(current => {
                foreach (var job in current.Jobs.Where(j => j.State == BookingQueueState.Running)) job.State = BookingQueueState.NeedsReview;
                return true;
            });
    }

    static string Timestamp() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    static bool ValidId(string value) { return value != null && Regex.IsMatch(value, @"\A[a-f0-9]{32}\z"); }
    static bool ValidTimestamp(string value)
    {
        DateTime parsed;
        return value != null && DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed) && parsed.Kind == DateTimeKind.Utc;
    }
    static void ValidateTargets(IList<BookingTarget> targets)
    {
        if (targets == null || targets.Count < 1 || targets.Count > MaximumTargets) throw new ArgumentException("한 예약에는 열차 1~20편을 선택하세요.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<int>();
        BookingTarget first = targets[0];
        foreach (var target in targets)
        {
            if (target == null || target.Validate() != null) throw new ArgumentException("날짜·구간·열차·등급이 정확한 1명 예약을 선택하세요.");
            int number = Int32.Parse(Regex.Match(target.Train, @"(\d{1,5})\s*$").Groups[1].Value, CultureInfo.InvariantCulture);
            if (number <= 0 || !keys.Add(target.Key) || !numbers.Add(number)) throw new ArgumentException("한 예약에 같은 열차를 중복으로 넣을 수 없습니다.");
            if (target.Date != first.Date || target.DepartureStation.Trim() != first.DepartureStation.Trim() || target.ArrivalStation.Trim() != first.ArrivalStation.Trim() || target.Grade != first.Grade || target.Passengers != first.Passengers)
                throw new ArgumentException("한 예약의 후보들은 같은 날짜·구간·좌석 등급·인원이어야 합니다.");
        }
    }
    static void Validate(StoredQueue data)
    {
        if (data == null || data.Version != 1 || data.Jobs == null || data.Jobs.Count > MaximumJobs) throw new InvalidDataException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        int active = 0;
        foreach (var job in data.Jobs)
        {
            if (job == null || !ValidId(job.Id) || !ids.Add(job.Id) || !ValidTimestamp(job.CreatedUtc) || !Enum.IsDefined(typeof(BookingQueueState), job.State)) throw new InvalidDataException();
            ValidateTargets(job.Targets);
            ValidateQuery(job.QueryJson, job.Targets);
            new RefreshOptions(job.IntervalSeconds, job.Limit, job.JitterPercent, job.Unlimited, job.CompletionMode);
            if (job.RefreshCount < 0) throw new InvalidDataException();
            if (job.Completions == null) job.Completions = new List<BookingQueueCompletion>();
            // Old completed Any jobs had only the final attempt binding.
            if (job.State == BookingQueueState.Completed && job.CompletionMode == BookingCompletionMode.Any && job.Completions.Count == 0)
                job.Completions.Add(new BookingQueueCompletion { AttemptId = job.AttemptId, TargetKey = job.CompletedTargetKey, ConfirmedUtc = job.CompletedUtc });
            if (job.Completions.Count > job.Targets.Count || job.CompletionMode == BookingCompletionMode.Any && job.Completions.Count > 1) throw new InvalidDataException();
            var completedKeys = new HashSet<string>(StringComparer.Ordinal);
            var completedAttempts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var completed in job.Completions)
            {
                if (completed == null || !ValidId(completed.AttemptId) || !completedAttempts.Add(completed.AttemptId) || !completedKeys.Add(completed.TargetKey) ||
                    !job.Targets.Any(t => t.Key == completed.TargetKey) || !ValidTimestamp(completed.ConfirmedUtc) || String.CompareOrdinal(completed.ConfirmedUtc, job.CreatedUtc) < 0)
                    throw new InvalidDataException();
                if (!attempts.Add(completed.AttemptId)) throw new InvalidDataException();
            }
            if (job.AttemptId != null && (!ValidId(job.AttemptId) || !completedAttempts.Contains(job.AttemptId) && !attempts.Add(job.AttemptId))) throw new InvalidDataException();
            if (job.AttemptId == null ? job.AttemptTargetKey != null : !job.Targets.Any(t => t.Key == job.AttemptTargetKey)) throw new InvalidDataException();
            if (job.AttemptId != null && completedAttempts.Contains(job.AttemptId) && !job.Completions.Any(c => c.AttemptId == job.AttemptId && c.TargetKey == job.AttemptTargetKey)) throw new InvalidDataException();
            if (job.State == BookingQueueState.Pending)
            {
                if (job.StartedUtc != null || job.AttemptId != null) throw new InvalidDataException();
            }
            else if (!ValidTimestamp(job.StartedUtc) || String.CompareOrdinal(job.StartedUtc, job.CreatedUtc) < 0) throw new InvalidDataException();
            if (job.State == BookingQueueState.Completed)
            {
                if (!ValidTimestamp(job.CompletedUtc) || String.CompareOrdinal(job.CompletedUtc, job.StartedUtc) < 0 || job.AttemptId == null || job.CompletedTargetKey != job.AttemptTargetKey) throw new InvalidDataException();
                if (job.CompletionMode == BookingCompletionMode.All ? job.Completions.Count != job.Targets.Count : job.Completions.Count != 1) throw new InvalidDataException();
            }
            else if (job.CompletedUtc != null || job.CompletedTargetKey != null) throw new InvalidDataException();
            else if (job.Completions.Count == job.Targets.Count || job.CompletionMode == BookingCompletionMode.Any && job.Completions.Count != 0) throw new InvalidDataException();
            if (job.State == BookingQueueState.Running || job.State == BookingQueueState.NeedsReview) active++;
        }
        if (active > 1) throw new InvalidDataException();
    }
    StoredQueue Read()
    {
        if (!File.Exists(path))
        {
            if (sawStore || File.Exists(path + ".initialized")) throw new InvalidDataException("예약 순서 파일이 없어졌습니다. 공식 예약 내역을 확인하기 전에는 순서를 재시작할 수 없습니다.");
            return new StoredQueue { Version = 1, Jobs = new List<BookingQueueJob>() };
        }
        sawStore = true;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException();
            var data = new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 32 }.Deserialize<StoredQueue>(File.ReadAllText(path, Encoding.UTF8));
            Validate(data);
            return data;
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is IOException) throw;
            throw new InvalidDataException("예약 순서 파일을 읽을 수 없습니다. 파일을 보존하고 공식 예약 내역을 확인하세요.");
        }
    }
    void Write(StoredQueue data)
    {
        Validate(data);
        byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 32 }.Serialize(data));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("예약 순서 파일이 너무 큽니다.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // The marker is durable before the first actual queue. A crash in
            // between is a reviewable error, never a silently empty queue.
            using (var marker = new FileStream(path + ".initialized", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) marker.Flush(true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            sawStore = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    T Change<T>(Func<StoredQueue, T> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var data = Read(); T result = change(data); Write(data); return result;
        }
    }
    static BookingQueueJob Require(StoredQueue data, string id)
    {
        var job = data.Jobs.FirstOrDefault(j => j.Id == id);
        if (job == null) throw new InvalidOperationException("예약 순서 항목을 찾을 수 없습니다.");
        return job;
    }
    public IList<BookingQueueJob> GetAll() { return Read().Jobs.Select(j => j.Copy()).ToList(); }
    public BookingQueueJob Get(string id) { var job = Read().Jobs.FirstOrDefault(j => j.Id == id); return job == null ? null : job.Copy(); }
    public BookingQueueJob Add(IEnumerable<BookingTarget> targets, RefreshOptions options)
    { return Add(targets, options, null); }
    static string DefaultQuery(IList<BookingTarget> targets)
    {
        var target = targets[0];
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
            { "date", target.Date }, { "departureStation", target.DepartureStation.Trim() }, { "arrivalStation", target.ArrivalStation.Trim() },
            { "fromTime", targets.Min(t => t.Departure) }, { "passengers", 1 }, { "route", "직통" }, { "seatAttribute", "일반석" },
            { "roundTrip", false }, { "adjacent", false }, { "combined", false }, { "supported", true }
        });
    }
    static void ValidateQuery(string json, IList<BookingTarget> targets)
    {
        if (String.IsNullOrWhiteSpace(json) || json.Length > 8000) throw new ArgumentException("조회 조건을 확인할 수 없습니다.");
        var query = new JavaScriptSerializer { MaxJsonLength = 8000, RecursionLimit = 8 }.DeserializeObject(json) as Dictionary<string, object>;
        string[] names = { "date", "departureStation", "arrivalStation", "fromTime", "passengers", "route", "seatAttribute", "roundTrip", "adjacent", "combined", "supported" };
        if (query == null || query.Count != names.Length || names.Any(name => !query.ContainsKey(name))) throw new ArgumentException("허용되지 않은 조회 조건이 있습니다.");
        var target = targets[0];
        if (!Object.Equals(query["date"], target.Date) || !Object.Equals(query["departureStation"], target.DepartureStation.Trim()) || !Object.Equals(query["arrivalStation"], target.ArrivalStation.Trim())
            || !Object.Equals(query["passengers"], 1) || !Object.Equals(query["route"], "직통") || !Object.Equals(query["supported"], true)
            || !Object.Equals(query["roundTrip"], false) || !Object.Equals(query["adjacent"], false) || !Object.Equals(query["combined"], false))
            throw new ArgumentException("조회 조건이 선택한 직통·1명 여정과 다릅니다.");
        var fromTime = query["fromTime"] as string;
        var seat = query["seatAttribute"] as string;
        if (fromTime == null || !Regex.IsMatch(fromTime, @"\A(?:[01]\d|2[0-3]):[0-5]\d\z") || targets.Any(t => String.CompareOrdinal(t.Departure, fromTime) < 0)
            || String.IsNullOrWhiteSpace(seat) || seat.Length > 40 || seat.Any(Char.IsControl)) throw new ArgumentException("조회 시간 또는 좌석 속성을 확인하세요.");
    }
    public static string NormalizeQuery(string queryJson, IList<BookingTarget> targets)
    {
        ValidateTargets(targets);
        ValidateQuery(queryJson, targets);
        var serializer = new JavaScriptSerializer { MaxJsonLength = 8000, RecursionLimit = 8 };
        var query = (Dictionary<string, object>)serializer.DeserializeObject(queryJson);
        var normalized = new Dictionary<string, object>();
        foreach (string name in new[] { "date", "departureStation", "arrivalStation", "fromTime", "passengers", "route", "seatAttribute", "roundTrip", "adjacent", "combined", "supported" }) normalized.Add(name, query[name]);
        return serializer.Serialize(normalized);
    }
    public BookingQueueJob Add(IEnumerable<BookingTarget> targets, RefreshOptions options, string queryJson)
    {
        if (targets == null || options == null) throw new ArgumentException("선택한 열차와 조회 설정이 필요합니다.");
        var frozen = targets.Take(MaximumTargets + 1).Select(t => t == null ? null : t.Copy()).ToList();
        ValidateTargets(frozen);
        string frozenQuery = NormalizeQuery(queryJson ?? DefaultQuery(frozen), frozen);
        return Change(data => {
            if (data.Jobs.Count >= MaximumJobs) throw new InvalidOperationException("예약 순서는 최대 20건입니다. 완료한 항목을 정리한 뒤 추가하세요.");
            var job = new BookingQueueJob { Id = Guid.NewGuid().ToString("N"), Targets = frozen, IntervalSeconds = options.IntervalSeconds, Limit = options.Limit, JitterPercent = options.JitterPercent,
                Unlimited = options.Unlimited, CompletionMode = options.CompletionMode, Completions = new List<BookingQueueCompletion>(), QueryJson = frozenQuery, State = BookingQueueState.Pending, CreatedUtc = Timestamp() };
            data.Jobs.Add(job); return job.Copy();
        });
    }
    public void Remove(string id)
    {
        Change(data => {
            var job = Require(data, id);
            if (job.State != BookingQueueState.Pending && job.State != BookingQueueState.Completed) throw new InvalidOperationException("실행 중이거나 확인이 필요한 예약 순서는 먼저 결과를 확인하세요.");
            data.Jobs.Remove(job); return true;
        });
    }
    public void Move(string id, int newIndex)
    {
        Change(data => {
            var job = Require(data, id);
            if (job.State != BookingQueueState.Pending) throw new InvalidOperationException("대기 중인 예약만 순서를 바꿀 수 있습니다.");
            if (newIndex < 0 || newIndex >= data.Jobs.Count) throw new ArgumentOutOfRangeException("newIndex");
            data.Jobs.Remove(job); data.Jobs.Insert(newIndex, job); return true;
        });
    }
    static BookingQueueJob BeginIn(StoredQueue data, string id, bool selected = false)
    {
        if (data.Jobs.Any(j => j.State == BookingQueueState.Running || j.State == BookingQueueState.NeedsReview)) throw new InvalidOperationException("진행 중인 예약이나 확인이 필요한 예약을 먼저 확인하세요.");
        var next = selected ? data.Jobs.FirstOrDefault(j => j.Id == id && j.State == BookingQueueState.Pending) : data.Jobs.FirstOrDefault(j => j.State == BookingQueueState.Pending);
        if (next == null) return null;
        if (id != null && next.Id != id) throw new InvalidOperationException("예약 순서의 첫 번째 대기 항목부터 실행해야 합니다.");
        next.State = BookingQueueState.Running; next.StartedUtc = Timestamp();
        return next.Copy();
    }
    public BookingQueueJob BeginNext() { return Change(data => BeginIn(data, null)); }
    public BookingQueueJob Begin(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("예약 순서 ID가 필요합니다.");
        return Change(data => { Require(data, id); return BeginIn(data, id); });
    }
    // Explicitly starting the current selection must not consume pre-existing
    // queued journeys. Ordinary Begin/BeginNext still enforce saved order.
    public BookingQueueJob BeginSelected(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("예약 순서 ID가 필요합니다.");
        return Change(data => { Require(data, id); return BeginIn(data, id, true); });
    }
    public void RecordRefreshCount(string id, long count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException("count");
        Change(data => {
            var job = Require(data, id);
            if (job.State != BookingQueueState.Running || count < job.RefreshCount) throw new InvalidOperationException("실행 중인 조회 횟수는 줄일 수 없습니다.");
            job.RefreshCount = count; return true;
        });
    }
    public void BindAttempt(string jobId, BookingAttempt attempt)
    {
        if (attempt == null || !ValidId(attempt.Id) || attempt.Target == null || attempt.Target.Validate() != null || attempt.State != BookingAttemptState.Prepared || !ValidTimestamp(attempt.CreatedUtc) || attempt.SubmittedUtc != null || attempt.Receipt != null)
            throw new ArgumentException("제출 전의 유효한 예약 시도 기록이 필요합니다.");
        var snapshot = attempt.Copy();
        Change(data => {
            var job = Require(data, jobId);
            if (job.State != BookingQueueState.Running || !job.RemainingTargets().Any(t => t.Key == snapshot.Target.Key)) throw new InvalidOperationException("실행 중인 예약의 남은 선택 열차와 예약 시도가 일치하지 않습니다.");
            if (job.AttemptId != null && job.AttemptId != snapshot.Id) throw new InvalidOperationException("이 항목에는 이미 예약 시도가 연결되어 있습니다.");
            if (job.AttemptTargetKey != null && job.AttemptTargetKey != snapshot.Target.Key) throw new InvalidOperationException("연결된 예약 시도의 열차를 변경할 수 없습니다.");
            if (data.Jobs.Any(j => j.Id != job.Id && (j.AttemptId == snapshot.Id || j.Completions.Any(c => c.AttemptId == snapshot.Id)))) throw new InvalidOperationException("다른 항목에 연결된 예약 시도입니다.");
            job.AttemptId = snapshot.Id; job.AttemptTargetKey = snapshot.Target.Key; return true;
        });
    }
    public bool Complete(string jobId, BookingAttempt attempt)
    {
        if (attempt == null || !ValidId(attempt.Id) || attempt.State != BookingAttemptState.Confirmed || attempt.Target == null || attempt.Target.Validate() != null || attempt.Receipt == null || attempt.Receipt.Validate(attempt.Target) != null || !ValidTimestamp(attempt.CreatedUtc) || !ValidTimestamp(attempt.SubmittedUtc) || !ValidTimestamp(attempt.ConfirmedUtc)
            || String.CompareOrdinal(attempt.CreatedUtc, attempt.SubmittedUtc) > 0 || String.CompareOrdinal(attempt.SubmittedUtc, attempt.ConfirmedUtc) > 0 || !Enum.IsDefined(typeof(BookingNotificationState), attempt.NotificationStatus) || attempt.NotificationStatus == BookingNotificationState.None)
            throw new ArgumentException("선택한 열차의 실제 예약 완료 기록이 필요합니다.");
        var snapshot = attempt.Copy();
        return Change(data => {
            var job = Require(data, jobId);
            var recorded = job.Completions.FirstOrDefault(c => c.AttemptId == snapshot.Id);
            if (recorded != null)
            {
                if (recorded.TargetKey != snapshot.Target.Key) throw new InvalidOperationException("이미 확인한 시도의 열차와 다른 예약 결과입니다.");
                return false;
            }
            if (job.AttemptId != snapshot.Id || job.AttemptTargetKey != snapshot.Target.Key || !job.Targets.Any(t => t.Key == snapshot.Target.Key)) throw new InvalidOperationException("이 예약 순서에 연결된 열차와 완료 기록이 일치하지 않습니다.");
            if (job.State == BookingQueueState.Completed)
            {
                if (job.CompletedTargetKey != snapshot.Target.Key) throw new InvalidOperationException("이미 완료한 열차와 다른 예약 결과입니다.");
                return false;
            }
            if (job.State != BookingQueueState.Running && job.State != BookingQueueState.NeedsReview) throw new InvalidOperationException("시작한 예약만 완료할 수 있습니다.");
            if (job.Completions.Any(c => c.TargetKey == snapshot.Target.Key)) throw new InvalidOperationException("이미 예약을 확인한 열차입니다.");
            job.Completions.Add(new BookingQueueCompletion { AttemptId = snapshot.Id, TargetKey = snapshot.Target.Key, ConfirmedUtc = snapshot.ConfirmedUtc });
            if (job.CompletionMode == BookingCompletionMode.All && job.Completions.Count < job.Targets.Count)
            {
                job.State = BookingQueueState.Pending; job.StartedUtc = null; job.AttemptId = null; job.AttemptTargetKey = null;
            }
            else { job.State = BookingQueueState.Completed; job.CompletedTargetKey = snapshot.Target.Key; job.CompletedUtc = Timestamp(); }
            return true;
        });
    }
    public void MarkNeedsReview(string id)
    {
        Change(data => {
            var job = Require(data, id);
            if (job.State != BookingQueueState.Running && job.State != BookingQueueState.NeedsReview) throw new InvalidOperationException("진행 중인 예약만 확인 대기로 바꿀 수 있습니다.");
            job.State = BookingQueueState.NeedsReview; return true;
        });
    }
    // This does not release any BookingAttemptStore lock. The host must still
    // check the official reservation and explicitly resolve that separate lock.
    public void ResetAfterUserConfirmed(string id, bool userConfirmed)
    {
        if (!userConfirmed) throw new InvalidOperationException("공식 예약 내역을 확인했다는 사용자 확인이 필요합니다.");
        Change(data => {
            var job = Require(data, id);
            if (job.State != BookingQueueState.NeedsReview) throw new InvalidOperationException("확인이 필요한 항목만 다시 대기시킬 수 있습니다.");
            job.State = BookingQueueState.Pending; job.StartedUtc = null; job.AttemptId = null; job.AttemptTargetKey = null; return true;
        });
    }
}
