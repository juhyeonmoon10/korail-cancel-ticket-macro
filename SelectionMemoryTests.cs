using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

// Local selection data only. These tests never open a browser or send messages.
internal static class SelectionMemoryTests
{
    static int count;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (InvalidDataException) { return true; }
    }
    static Dictionary<string, object> Query()
    {
        return new Dictionary<string, object> {
            { "supported", true }, { "date", "2026-10-25" }, { "departureStation", "서울" }, { "arrivalStation", "부산" },
            { "fromTime", "00:00" }, { "passengers", 1 }, { "route", "직통" }, { "seatAttribute", "일반석" },
            { "roundTrip", false }, { "adjacent", false }, { "combined", false }, { "reason", "" }
        };
    }
    static Dictionary<string, object> Selection(bool confirmed)
    {
        return new Dictionary<string, object> { { "confirmed", confirmed }, { "targets", new[] {
            new Dictionary<string, object> { { "train", "KTX 103" }, { "departure", "07:30" }, { "arrival", "10:15" }, { "grade", "일반실" }, { "autoCapable", true } },
            new Dictionary<string, object> { { "train", "KTX 101" }, { "departure", "08:00" }, { "arrival", "10:45" }, { "grade", "일반실" }, { "autoCapable", true } }
        } } };
    }
    static int Main(string[] args)
    {
        try
        {
            string root = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "KorailSelectionMemoryTests");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            Run(directory);
            Console.WriteLine("Passed " + count + " selection memory checks (local files only)"); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run(string directory)
    {
        string path = Path.Combine(directory, "selected-trains.json");
        Check(SavedTrainSelection.Load(path) == null && !File.Exists(path), "missing selection remains empty without creating files");
        var message = Selection(false); var query = Query();
        message["token"] = "SYNTHETIC_BOT_SECRET"; message["password"] = "SYNTHETIC_ACCOUNT_SECRET";
        query["reason"] = "SYNTHETIC_PRIVATE_SITE_MESSAGE"; query["cookies"] = "SYNTHETIC_BROWSER_SECRET";
        var saved = SavedTrainSelection.FromMessage(message, query);
        Check(saved.Version == 1 && !saved.Confirmed && saved.Targets.Count == 2, "applied unconfirmed choices are retained without manufacturing confirmation");
        Check(saved.Targets.Select(t => t.Train).SequenceEqual(new[] { "KTX 103", "KTX 101" }), "candidate priority is retained exactly");
        Check(saved.Targets.All(t => t.Date == "2026-10-25" && t.DepartureStation == "서울" && t.ArrivalStation == "부산" && t.Passengers == 1), "native targets include their complete journey identity");
        saved.Save(path);
        string stored = File.ReadAllText(path);
        Check(!stored.Contains("SYNTHETIC_") && !stored.Contains("password") && !stored.Contains("cookies") && !stored.Contains("BotToken"), "selection persistence excludes arbitrary page fields and credentials");
        var loaded = SavedTrainSelection.Load(path);
        Check(loaded.Targets.Select(t => t.Key).SequenceEqual(saved.Targets.Select(t => t.Key)) && loaded.QueryJson == saved.QueryJson && !loaded.Confirmed, "unconfirmed selection and full query survive disk reload");
        var storedQuery = Json.Deserialize<Dictionary<string, object>>(loaded.QueryJson);
        Check(storedQuery.Count == 11 && !storedQuery.ContainsKey("reason") && Object.Equals(storedQuery["fromTime"], "00:00") && Object.Equals(storedQuery["seatAttribute"], "일반석"), "stored query uses the complete eleven-field allowlist");

        message["confirmed"] = true;
        saved = SavedTrainSelection.FromMessage(message, query); saved.Save(path);
        Check(SavedTrainSelection.Load(path).Confirmed && !File.Exists(path + ".tmp"), "confirmed update replaces the prior file atomically without a leftover temporary file");
        var page = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(saved.ForPage()));
        var targets = (IList)page["targets"];
        var first = (Dictionary<string, object>)targets[0];
        Check(page.Count == 3 && first.Count == 5 && Object.Equals(first["train"], "KTX 103") && Object.Equals(first["autoCapable"], true) && Object.Equals(page["confirmed"], true), "page restoration exports only its query, confirmation and plain train identities");
        first["train"] = "KTX 999";
        Check(saved.Targets[0].Train == "KTX 103", "restoration payload is detached from the native stored selection");
        var falseConfirmation = Selection(false); falseConfirmation["confirmed"] = "true";
        Check(!SavedTrainSelection.FromMessage(falseConfirmation, Query()).Confirmed, "only a boolean true retains user confirmation");
        falseConfirmation.Remove("confirmed");
        Check(!SavedTrainSelection.FromMessage(falseConfirmation, Query()).Confirmed, "missing confirmation remains unchecked");

        Check(Reject(() => SavedTrainSelection.FromMessage(null, Query())) && Reject(() => SavedTrainSelection.FromMessage(Selection(true), null)), "missing selection or query is rejected");
        foreach (string field in new[] { "fromTime", "date", "departureStation", "arrivalStation", "passengers", "route", "seatAttribute", "roundTrip", "adjacent", "combined", "supported" })
        {
            var incomplete = Query(); incomplete.Remove(field);
            Check(Reject(() => SavedTrainSelection.FromMessage(Selection(true), incomplete)), "incomplete query field rejected: " + field);
        }
        var invalid = Query(); invalid["passengers"] = 2;
        Check(Reject(() => SavedTrainSelection.FromMessage(Selection(true), invalid)), "two-passenger query cannot become saved one-passenger intent");
        invalid = Query(); invalid["route"] = "환승";
        Check(Reject(() => SavedTrainSelection.FromMessage(Selection(true), invalid)), "unsupported transfer query rejected");
        invalid = Query(); invalid["fromTime"] = "09:00";
        Check(Reject(() => SavedTrainSelection.FromMessage(Selection(true), invalid)), "query time excluding a chosen train is rejected");
        invalid = Query(); invalid["adjacent"] = true;
        Check(Reject(() => SavedTrainSelection.FromMessage(Selection(true), invalid)), "adjacent-station query rejected");
        var malformed = Selection(true); var badTargets = (Dictionary<string, object>[])malformed["targets"];
        badTargets[1]["train"] = "KTX 103";
        Check(Reject(() => SavedTrainSelection.FromMessage(malformed, Query())), "duplicate candidate number rejected before persistence");
        malformed = Selection(true); badTargets = (Dictionary<string, object>[])malformed["targets"]; badTargets[0]["autoCapable"] = false;
        Check(Reject(() => SavedTrainSelection.FromMessage(malformed, Query())), "unsupported seat target is not retained for restoration");

        var store = new BookingQueueStore(Path.Combine(directory, "queue.json"));
        var job = store.Add(saved.Targets, new RefreshOptions(5, 9999999, 0), saved.QueryJson);
        var next = SavedTrainSelection.FromJob(job);
        Check(next.Confirmed && next.Targets.Select(t => t.Key).SequenceEqual(saved.Targets.Select(t => t.Key)) && next.QueryJson == saved.QueryJson, "queued job converts to its original confirmed journey and ordered choices");
        job.Targets[0].Train = "KTX 999";
        Check(next.Targets[0].Train == "KTX 103", "queued restoration is detached from mutable job snapshots");

        File.WriteAllText(path, "{truncated", Encoding.UTF8);
        Check(Reject(() => SavedTrainSelection.Load(path)), "corrupt selection file cannot create an empty or widened choice");
        saved.Version = 99; File.WriteAllText(path, Json.Serialize(saved), Encoding.UTF8);
        Check(Reject(() => SavedTrainSelection.Load(path)), "future selection version is rejected");
        saved.Version = 1; saved.Targets[0].Passengers = 2; File.WriteAllText(path, Json.Serialize(saved), Encoding.UTF8);
        Check(Reject(() => SavedTrainSelection.Load(path)), "tampered persisted target is rejected on load");
        File.WriteAllText(path, new string(' ', 65537), Encoding.UTF8);
        Check(Reject(() => SavedTrainSelection.Load(path)), "oversized selection file is rejected before parsing");
    }
}
