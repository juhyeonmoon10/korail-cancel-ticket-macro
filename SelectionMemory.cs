using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class SavedTrainSelection
{
    public int Version { get; set; }
    public List<BookingTarget> Targets { get; set; }
    public string QueryJson { get; set; }
    public bool Confirmed { get; set; }
    static readonly string[] QueryFields = { "date", "departureStation", "arrivalStation", "fromTime", "passengers", "route", "seatAttribute", "roundTrip", "adjacent", "combined", "supported" };

    public static string QueryText(Dictionary<string, object> query, IList<BookingTarget> targets)
    {
        if (query == null) throw new ArgumentException("조회 조건을 읽지 못했습니다.");
        var allowed = new Dictionary<string, object>();
        foreach (string key in QueryFields)
        {
            object value; if (!query.TryGetValue(key, out value)) throw new ArgumentException("조회 조건이 완전하지 않습니다.");
            allowed[key] = value;
        }
        return BookingQueueStore.NormalizeQuery(new JavaScriptSerializer().Serialize(allowed), targets);
    }

    public static SavedTrainSelection FromMessage(Dictionary<string, object> selection, Dictionary<string, object> query)
    {
        if (selection == null) throw new ArgumentException("선택한 열차가 없습니다.");
        object confirmed;
        bool checkedByUser = selection.TryGetValue("confirmed", out confirmed) && Object.Equals(confirmed, true);
        var validate = new Dictionary<string, object>(selection); validate["confirmed"] = true;
        var targets = BookingSelection.FromSnapshot(validate, query);
        return new SavedTrainSelection { Version = 1, Targets = targets, QueryJson = QueryText(query, targets), Confirmed = checkedByUser };
    }

    public object ForPage()
    {
        return new { query = new JavaScriptSerializer().DeserializeObject(QueryJson), confirmed = Confirmed,
            targets = Targets.Select(t => new { train = t.Train, departure = t.Departure, arrival = t.Arrival, grade = t.Grade, autoCapable = true }).ToArray() };
    }

    public static SavedTrainSelection FromJob(BookingQueueJob job)
    {
        return new SavedTrainSelection { Version = 1, Targets = job.Targets.Select(t => t.Copy()).ToList(),
            QueryJson = BookingQueueStore.NormalizeQuery(job.QueryJson, job.Targets), Confirmed = true };
    }

    public static SavedTrainSelection Load(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("저장된 열차 선택을 읽지 못했습니다.");
        var value = new JavaScriptSerializer().Deserialize<SavedTrainSelection>(File.ReadAllText(path, Encoding.UTF8));
        if (value == null || value.Version != 1) throw new InvalidDataException("저장된 열차 선택의 형식을 확인하세요.");
        value.QueryJson = BookingQueueStore.NormalizeQuery(value.QueryJson, value.Targets);
        return value;
    }

    public void Save(string path)
    {
        QueryJson = BookingQueueStore.NormalizeQuery(QueryJson, Targets);
        string temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
}
