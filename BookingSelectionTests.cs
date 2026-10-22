using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

internal static class BookingSelectionTests
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int checks;

    static int Main()
    {
        try { Run(); Console.WriteLine("PASS " + checks + " booking selection checks (local JSON only)"); return 0; }
        catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        checks++; Console.WriteLine("PASS " + name);
    }
    static void Rejected(Action action, string name)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidOperationException) { rejected = true; }
        catch (ArgumentException) { rejected = true; }
        catch (FormatException) { rejected = true; }
        Check(rejected, name);
    }
    static Dictionary<string, object> Read(string source)
    { return Json.Deserialize<Dictionary<string, object>>(source); }
    static Dictionary<string, object> Entry(string train = "KTX 505", string departure = "09:00", string arrival = "11:40", string grade = "일반실")
    {
        return new Dictionary<string, object> { { "autoCapable", true }, { "train", train },
            { "departure", departure }, { "arrival", arrival }, { "grade", grade } };
    }
    static Dictionary<string, object> Query()
    { return Read("{\"supported\":true,\"passengers\":1,\"date\":\"2026-10-25\",\"departureStation\":\"서울\",\"arrivalStation\":\"부산\"}"); }
    static Dictionary<string, object> Selection(params Dictionary<string, object>[] targets)
    { return new Dictionary<string, object> { { "confirmed", true }, { "targets", targets } }; }
    static List<BookingTarget> Parse(Dictionary<string, object> selection, Dictionary<string, object> query)
    {
        // Match the real WebView bridge: JavaScript arrays and numbers are
        // deserialized by JavaScriptSerializer before the selection is checked.
        var payload = Read(Json.Serialize(new { selection, query }));
        return BookingSelection.FromSnapshot(payload["selection"] as Dictionary<string, object>, payload["query"] as Dictionary<string, object>);
    }
    static Dictionary<string, object> Observed(BookingTarget target)
    {
        return Read(Json.Serialize(new { date = target.Date, departureStation = target.DepartureStation,
            arrivalStation = target.ArrivalStation, train = target.Train, departure = target.Departure,
            arrival = target.Arrival, grade = target.Grade, passengers = target.Passengers }));
    }
    static BookingTarget Match(Dictionary<string, object> observed, IList<BookingTarget> candidates)
    { return BookingSelection.MatchSelected(observed == null ? null : Read(Json.Serialize(observed)), candidates); }

    static void Run()
    {
        var entries = new [] { Entry("KTX 561", "12:00", "14:40"), Entry("KTX-산천 541", "10:00", "12:40"), Entry() };
        var candidates = Parse(Selection(entries), Query());
        Check(candidates.Count == 3 && candidates[0].Train == "KTX 561" && candidates[1].Train == "KTX-산천 541" && candidates[2].Train == "KTX 505",
            "actual JSON array preserves chosen priority instead of sorting by number or time");
        Check(candidates.All(target => target.Date == "2026-10-25" && target.DepartureStation == "서울" && target.ArrivalStation == "부산" && target.Grade == "일반실" && target.Passengers == 1),
            "all alternatives use the confirmed one-passenger query");
        entries[0]["train"] = "KTX 999";
        Check(candidates[0].Train == "KTX 561", "candidate objects are independent of the source snapshot");
        var oldSelection = new Dictionary<string, object> { { "confirmed", true }, { "target", Entry() } };
        Check(Parse(oldSelection, Query()).Single().Train == "KTX 505", "older single-target snapshot remains supported");
        oldSelection["targets"] = new object[0];
        Rejected(() => Parse(oldSelection, Query()), "explicit empty targets cannot fall back to a stale single target");

        Rejected(() => Parse(null, Query()), "null selection rejected");
        Rejected(() => Parse(Selection(Entry()), null), "null query rejected");
        foreach (object value in new object[] { false, "true", 1, null })
        {
            var selection = Selection(Entry()); selection["confirmed"] = value;
            Rejected(() => Parse(selection, Query()), "confirmation must be a JSON true boolean: " + (value ?? "null"));
            var query = Query(); query["supported"] = value;
            Rejected(() => Parse(Selection(Entry()), query), "query support must be a JSON true boolean: " + (value ?? "null"));
        }
        var missingConfirmation = Selection(Entry()); missingConfirmation.Remove("confirmed");
        Rejected(() => Parse(missingConfirmation, Query()), "missing confirmation rejected");
        foreach (object passengers in new object[] { 0, 2, -1, "1", true, null })
        {
            var query = Query(); query["passengers"] = passengers;
            Rejected(() => Parse(Selection(Entry()), query), "query must explicitly contain one numeric passenger: " + (passengers ?? "null"));
        }
        var noPassengers = Query(); noPassengers.Remove("passengers");
        Rejected(() => Parse(Selection(Entry()), noPassengers), "missing passenger count rejected");

        foreach (object targets in new object[] { null, "505,541", 505, Entry(), new object[0], new object[] { null }, new object[] { 505 } })
        {
            var selection = Selection(Entry()); selection["targets"] = targets;
            Rejected(() => Parse(selection, Query()), "malformed or empty target collection rejected");
        }
        var many = Enumerable.Range(1, 20).Select(number => Entry("KTX " + number)).ToArray();
        Check(Parse(Selection(many), Query()).Count == 20, "twenty candidates accepted");
        Rejected(() => Parse(Selection(many.Concat(new [] { Entry("KTX 21") }).ToArray()), Query()), "twenty-one candidates rejected");
        Rejected(() => Parse(Selection(Entry(), Entry()), Query()), "repeated target rejected");
        Rejected(() => Parse(Selection(Entry(), Entry("KTX-산천 00505", "10:00", "12:40")), Query()), "same normalized number with a different type or time is ambiguous");
        Rejected(() => Parse(Selection(Entry("KTX 0")), Query()), "zero train number rejected");
        Rejected(() => Parse(Selection(Entry("KTX 100000")), Query()), "six-digit train number rejected");
        Rejected(() => Parse(Selection(Entry(), Entry("KTX 541", "10:00", "12:40", "특실")), Query()), "mixed grades rejected");
        foreach (object capability in new object[] { false, "true", 1, null })
        {
            var item = Entry(); item["autoCapable"] = capability;
            Rejected(() => Parse(Selection(item), Query()), "candidate must explicitly support automatic booking");
        }
        foreach (var change in new Dictionary<string, object> { { "date", "2026-02-30" }, { "departureStation", "" }, { "arrivalStation", "서울" } })
        {
            var query = Query(); query[change.Key] = change.Value;
            Rejected(() => Parse(Selection(Entry()), query), "invalid confirmed query rejected: " + change.Key);
        }
        foreach (var change in new Dictionary<string, object> { { "train", 505 }, { "departure", "25:00" }, { "arrival", "11:90" }, { "grade", "입석" } })
        {
            var item = Entry(); item[change.Key] = change.Value;
            Rejected(() => Parse(Selection(item), Query()), "invalid target field rejected: " + change.Key);
        }

        var canonical = candidates[1];
        var exact = Match(Observed(canonical), candidates);
        Check(exact.Key == canonical.Key && exact.Train == canonical.Train && !Object.ReferenceEquals(exact, canonical), "selected alternative returns an independent canonical copy");
        exact.Train = "KTX 999"; exact.DepartureStation = "광명";
        Check(canonical.Train == "KTX-산천 541" && canonical.DepartureStation == "서울", "mutating the returned target cannot change the approved list");
        var padded = Observed(canonical); padded["departureStation"] = " 서울 "; padded["untrustedMetadata"] = "ignored";
        var cleaned = Match(padded, candidates);
        Check(cleaned.DepartureStation == "서울" && cleaned.Key == canonical.Key, "matching normalized evidence returns approved canonical fields rather than page fields");
        foreach (var change in new Dictionary<string, object> {
            { "date", "2026-10-26" }, { "departureStation", "광명" }, { "arrivalStation", "울산" },
            { "departure", "10:01" }, { "arrival", "12:41" }, { "train", "KTX 541" }, { "grade", "특실" }, { "passengers", 2 } })
        {
            var selected = Observed(canonical); selected[change.Key] = change.Value;
            Rejected(() => Match(selected, candidates), "changed selected candidate rejected: " + change.Key);
        }
        var outside = Observed(canonical); outside["train"] = "KTX-산천 543";
        Rejected(() => Match(outside, candidates), "candidate outside the approved list rejected");
        foreach (object passenger in new object[] { "1", true, null })
        {
            var selected = Observed(canonical); selected["passengers"] = passenger;
            Rejected(() => Match(selected, candidates), "selected target passenger field is type checked");
        }
        foreach (string field in new [] { "date", "train", "departureStation", "arrivalStation", "departure", "arrival", "grade", "passengers" })
        {
            var selected = Observed(canonical); selected.Remove(field);
            Rejected(() => Match(selected, candidates), "incomplete selected candidate rejected: " + field);
        }
        var duplicateCandidates = new List<BookingTarget> { canonical, canonical.Copy() };
        Rejected(() => Match(Observed(canonical), duplicateCandidates), "ambiguous canonical matches rejected");
        Rejected(() => Match(Observed(canonical), new List<BookingTarget>()), "empty canonical candidate list rejected");
        Rejected(() => Match(Observed(canonical), null), "null canonical candidate list rejected");
        Rejected(() => Match(null, candidates), "null selected target rejected");
    }
}
