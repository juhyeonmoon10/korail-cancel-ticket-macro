using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

// A selection is an ordered list of alternatives for one passenger and one
// journey. Only the candidate frozen at the first seat click can be submitted.
internal static class BookingSelection
{
    public static List<BookingTarget> FromSnapshot(Dictionary<string, object> selection, Dictionary<string, object> query)
    {
        if (!Flag(selection, "confirmed") || !Flag(query, "supported") || !OnePassenger(query))
            throw new InvalidOperationException("직통·1명으로 조회한 뒤 원하는 열차들을 적용하고 여정 확인란을 체크하세요.");
        object raw;
        var choices = new List<Dictionary<string, object>>();
        if (selection.TryGetValue("targets", out raw))
        {
            var values = raw as IEnumerable;
            if (values == null || raw is string || raw is IDictionary)
                throw new InvalidOperationException("열차 선택 목록을 읽지 못했습니다. 다시 적용하세요.");
            foreach (object value in values)
            {
                var item = value as Dictionary<string, object>;
                if (item == null || choices.Count >= 20) throw new InvalidOperationException("열차는 최대 20편까지 선택할 수 있습니다.");
                choices.Add(item);
            }
        }
        else
        {
            // Retain compatibility with an earlier single-target snapshot.
            if (!selection.TryGetValue("target", out raw) || !(raw is Dictionary<string, object>))
                throw new InvalidOperationException("열차 목록에서 원하는 열차를 적용하세요.");
            choices.Add((Dictionary<string, object>)raw);
        }
        if (choices.Count == 0) throw new InvalidOperationException("열차 목록에서 원하는 열차를 적용하세요.");
        var result = new List<BookingTarget>();
        var numbers = new HashSet<int>();
        foreach (var item in choices)
        {
            if (!Flag(item, "autoCapable")) throw new InvalidOperationException("자동 예약 가능한 열차 목록에서 선택하세요.");
            var target = new BookingTarget {
                Date = Text(query, "date"), DepartureStation = Text(query, "departureStation"), ArrivalStation = Text(query, "arrivalStation"),
                Train = Text(item, "train"), Departure = Text(item, "departure"), Arrival = Text(item, "arrival"), Grade = Text(item, "grade"), Passengers = 1
            };
            string issue = target.Validate();
            if (issue != null) throw new InvalidOperationException(issue);
            int number = Int32.Parse(Regex.Match(target.Train, @"(\d{1,5})\s*$").Groups[1].Value, CultureInfo.InvariantCulture);
            if (number <= 0 || !numbers.Add(number)) throw new InvalidOperationException("열차번호가 중복되거나 모호합니다. 목록을 다시 읽고 선택하세요.");
            if (result.Count > 0 && target.Grade != result[0].Grade) throw new InvalidOperationException("한 번의 실행에서는 같은 좌석 등급을 선택하세요.");
            result.Add(target);
        }
        return result;
    }

    public static BookingTarget MatchSelected(Dictionary<string, object> value, IList<BookingTarget> candidates)
    {
        if (value == null || candidates == null || !OnePassenger(value))
            throw new InvalidOperationException("가격을 클릭할 열차를 확인하지 못했습니다.");
        var observed = new BookingTarget {
            Date = Text(value, "date"), DepartureStation = Text(value, "departureStation"), ArrivalStation = Text(value, "arrivalStation"),
            Train = Text(value, "train"), Departure = Text(value, "departure"), Arrival = Text(value, "arrival"), Grade = Text(value, "grade"), Passengers = 1
        };
        if (observed.Validate() != null) throw new InvalidOperationException("가격을 클릭할 열차 정보가 올바르지 않습니다.");
        var matches = candidates.Where(target => target.Key == observed.Key && target.Train == observed.Train).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("처음 선택한 열차 목록과 가격 선택 대상이 다릅니다.");
        return matches[0].Copy();
    }

    static string Text(Dictionary<string, object> value, string name)
    { object item; return value != null && value.TryGetValue(name, out item) ? item as string : null; }
    static bool Flag(Dictionary<string, object> value, string name)
    { object item; return value != null && value.TryGetValue(name, out item) && Object.Equals(item, true); }
    static bool OnePassenger(Dictionary<string, object> value)
    { object item; return value != null && value.TryGetValue("passengers", out item) && Object.Equals(item, 1); }
}
