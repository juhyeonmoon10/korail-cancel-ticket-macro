using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class SettingsTests
{
    const string FakeToken = "123456789:THIS_IS_A_SYNTHETIC_TEST_TOKEN_12345";
    static int count;
    [STAThread]
    static int Main(string[] args)
    {
        try { Run(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "korail-settings-tests") : args[0]).GetAwaiter().GetResult(); Console.WriteLine("PASS " + count + " settings and Telegram checks (mock HTTP only)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message.Replace(FakeToken, "[redacted]")); return 1; }
    }
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static bool RejectsTrainSelection(string value)
    {
        try { TrainNumberSelection.Parse(value); return false; }
        catch (FormatException ex) { return !String.IsNullOrWhiteSpace(ex.Message); }
    }
    static TelegramSettings Valid() { return new TelegramSettings { Enabled = true, BotToken = FakeToken, ChatId = "-1001234567890" }; }
    static T Field<T>(SettingsDialog dialog, string name) where T : class
    {
        return (T)typeof(SettingsDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
    }
    static AppSettings ReadDialog(SettingsDialog dialog)
    {
        return (AppSettings)typeof(SettingsDialog).GetMethod("ReadFields", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, null);
    }
    static void CheckReportDialog()
    {
        var source = new AppSettings { Telegram = Valid() };
        source.Telegram.StartNotificationsEnabled = false; source.Telegram.ErrorNotificationsEnabled = true;
        source.Telegram.ProgressNotificationsEnabled = false; source.Telegram.ProgressEveryQueries = 137;
        using (var dialog = new SettingsDialog(source, true))
        {
            var read = ReadDialog(dialog).Telegram;
            Check(read.Enabled && !read.StartNotificationsEnabled && read.ErrorNotificationsEnabled && !read.ProgressNotificationsEnabled && read.ProgressEveryQueries == 137,
                "report dialog loads and reads each saved preference independently");
            var master = Field<CheckBox>(dialog, "telegramEnabled");
            var start = Field<CheckBox>(dialog, "startNotifications");
            var errors = Field<CheckBox>(dialog, "errorNotifications");
            var progress = Field<CheckBox>(dialog, "progressNotifications");
            var every = Field<NumericUpDown>(dialog, "progressEveryQueries");
            Check(every.Minimum == 1 && every.Maximum == 1000000 && every.DecimalPlaces == 0 && !every.Enabled,
                "report interval control accepts only bounded whole counts and follows periodic toggle");
            progress.Checked = true; every.Value = 50; start.Checked = true;
            read = ReadDialog(dialog).Telegram;
            Check(every.Enabled && read.ProgressNotificationsEnabled && read.StartNotificationsEnabled && read.ProgressEveryQueries == 50,
                "editing report choices and interval is reflected in saved dialog result");
            master.Checked = false;
            read = ReadDialog(dialog).Telegram;
            Check(!start.Enabled && !errors.Enabled && !progress.Enabled && !every.Enabled && !read.Enabled && read.StartNotificationsEnabled && read.ProgressNotificationsEnabled && read.ProgressEveryQueries == 50,
                "master notification toggle disables controls without losing report choices");
            Check(read.HasCredentials && read.BotToken == FakeToken && read.ChatId == source.Telegram.ChatId,
                "turning off report controls keeps encrypted-credential input intact");
            master.Checked = true;
            Check(start.Enabled && errors.Enabled && progress.Enabled && every.Enabled, "master notification toggle restores report controls");
            Check(!source.Telegram.StartNotificationsEnabled && !source.Telegram.ProgressNotificationsEnabled && source.Telegram.ProgressEveryQueries == 137,
                "dialog edits do not mutate initial settings before save");
        }
        source.Telegram.ProgressEveryQueries = -1;
        using (var dialog = new SettingsDialog(source, true))
            Check(ReadDialog(dialog).Telegram.ProgressEveryQueries == 50 && ReadDialog(dialog).Telegram.BotToken == FakeToken,
                "dialog normalizes malformed interval without discarding credentials");
    }
    static string WithReportFields(string json, IDictionary<string, object> fields)
    {
        var serializer = new JavaScriptSerializer();
        var document = serializer.Deserialize<Dictionary<string, object>>(json);
        var telegram = (Dictionary<string, object>)document["Telegram"];
        foreach (string key in new [] { "StartNotificationsEnabled", "ErrorNotificationsEnabled", "ProgressNotificationsEnabled", "ProgressEveryQueries" }) telegram.Remove(key);
        if (fields != null) foreach (var field in fields) telegram[field.Key] = field.Value;
        return serializer.Serialize(document);
    }
    static async Task Run(string directory)
    {
        CheckReportDialog();
        // These controls are inspected without a message loop; HTTP mocks below run as console tasks.
        SynchronizationContext.SetSynchronizationContext(null);
        var defaults = new TelegramSettings();
        Check(!defaults.Enabled && defaults.StartNotificationsEnabled && defaults.ErrorNotificationsEnabled && defaults.ProgressNotificationsEnabled && defaults.ProgressEveryQueries == 50,
            "new report preferences default on at fifty queries while master remains off");
        var copied = Valid(); copied.StartNotificationsEnabled = false; copied.ErrorNotificationsEnabled = false; copied.ProgressNotificationsEnabled = false; copied.ProgressEveryQueries = 137;
        var copy = copied.Copy();
        Check(!copy.StartNotificationsEnabled && !copy.ErrorNotificationsEnabled && !copy.ProgressNotificationsEnabled && copy.ProgressEveryQueries == 137 && copy.BotToken == FakeToken,
            "Telegram copy preserves all report preferences and credentials");
        copy.ProgressEveryQueries = 50; copy.StartNotificationsEnabled = true;
        Check(copied.ProgressEveryQueries == 137 && !copied.StartNotificationsEnabled, "Telegram copied report settings remain independent");
        foreach (int invalidInterval in new [] { -1, 0, 1000001, Int32.MaxValue })
        {
            var invalidReports = Valid(); invalidReports.ProgressEveryQueries = invalidInterval;
            Check(invalidReports.Validate() != null && invalidReports.HasCredentials, "invalid enabled report interval rejected without invalidating credentials");
            invalidReports.ProgressNotificationsEnabled = false;
            Check(invalidReports.Validate() == null, "disabled progress reports ignore inactive interval validation");
        }
        foreach (int interval in new [] { 1, 50, 1000000 })
        {
            var bounded = Valid(); bounded.ProgressEveryQueries = interval;
            Check(bounded.Validate() == null, "valid report interval accepted: " + interval);
        }
        Check(!new TelegramSettings().HasCredentials, "first run with no credentials requires Telegram setup");
        var setup = Valid();
        Check(setup.HasCredentials, "valid stored credentials skip first-run setup");
        setup.Enabled = false;
        Check(setup.HasCredentials && !setup.Enabled, "disabled alerts preserve setup completion without enabling alerts");
        setup.BotToken = "bad";
        Check(!setup.HasCredentials, "invalid disabled token requires setup");
        setup = Valid(); setup.ChatId = "0";
        Check(!setup.HasCredentials, "invalid Chat ID requires setup");
        setup.ChatId = "";
        Check(!setup.HasCredentials, "incomplete connection requires setup");
        var settings = new AppSettings { Journey = new JourneySettings { Enabled = true, DepartureStation = "서울", ArrivalStation = "부산", Date = "2026-10-25", FromTime = "09:00", ToTime = "12:00", TrainNumber = "101", Grade = "일반실" }, Telegram = Valid() };
        Check(settings.Journey.Validate() == null, "journey validates");
        Check(TrainNumberSelection.Parse(null).Count == 0 && TrainNumberSelection.Parse("").Count == 0 && TrainNumberSelection.Parse(" \t ").Count == 0,
            "blank train selection leaves the number filter unrestricted");
        var numbers = TrainNumberSelection.Parse(" 00505, 541,505, 00561,541 ");
        Check(numbers.Count == 3 && numbers[0] == 505 && numbers[1] == 541 && numbers[2] == 561,
            "CSV train numbers trim normalize and deduplicate in input order");
        Check(numbers.Contains(Int32.Parse("00541")) && !numbers.Contains(543), "normalized number selection includes candidates and excludes other trains");
        var bounds = TrainNumberSelection.Parse("00001,99999");
        Check(bounds.Count == 2 && bounds[0] == 1 && bounds[1] == 99999, "positive one-to-five digit number bounds accepted");
        foreach (string invalidNumber in new [] { "0", "00000", "100000", "000001", ",505", "505,", "505,,541", "505, ,541", "KTX505", "505/541", "5 05", "505.0", "+505", "-505", "５０５", "505\n541" })
        {
            Check(RejectsTrainSelection(invalidNumber), "invalid CSV train selection rejected: " + invalidNumber.Replace("\n", "\\n"));
            var rejectedJourney = settings.Journey.Copy(); rejectedJourney.TrainNumber = invalidNumber;
            Check(rejectedJourney.Validate() != null, "journey validation uses the shared number selection rules");
        }
        var twenty = new List<string>(); for (int i = 1; i <= 20; i++) twenty.Add(i.ToString());
        string maximumSelection = String.Join(",", twenty);
        Check(TrainNumberSelection.Parse(maximumSelection).Count == 20, "twenty unique train numbers accepted");
        Check(TrainNumberSelection.Parse(maximumSelection + ",00001,00020").Count == 20, "duplicate tokens do not consume the unique train limit");
        Check(RejectsTrainSelection(maximumSelection + ",21"), "twenty-one unique train numbers rejected");
        var invalidJourney = settings.Journey.Copy(); invalidJourney.ToTime = "08:00";
        Check(invalidJourney.Validate() != null, "reversed departure window rejected");
        invalidJourney = settings.Journey.Copy(); invalidJourney.TrainNumber = "101/../../";
        Check(invalidJourney.Validate() != null, "invalid train number rejected");
        Directory.CreateDirectory(directory); string file = Path.Combine(directory, "settings.json"); settings.Save(file);
        string stored = File.ReadAllText(file);
        Check(!stored.Contains(FakeToken) && stored.Contains("BotTokenProtected") && !stored.Contains("\"BotToken\":"), "token absent from persisted plaintext");
        var loaded = AppSettings.Load(file);
        Check(loaded.Telegram.BotToken == FakeToken && loaded.Telegram.Enabled && loaded.Journey.TrainNumber == "101", "DPAPI and journey roundtrip");
        Check(loaded.Telegram.HasCredentials, "saved connection skips setup after restart");
        string reportFile = Path.Combine(directory, "settings-reports.json");
        var reports = settings.Copy(); reports.Telegram.StartNotificationsEnabled = false; reports.Telegram.ErrorNotificationsEnabled = false;
        reports.Telegram.ProgressNotificationsEnabled = true; reports.Telegram.ProgressEveryQueries = 137;
        reports.Save(reportFile); var reportLoaded = AppSettings.Load(reportFile);
        Check(!reportLoaded.Telegram.StartNotificationsEnabled && !reportLoaded.Telegram.ErrorNotificationsEnabled && reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 137 && reportLoaded.LoadWarning == null,
            "report choices and nondefault interval roundtrip beside existing settings");
        Check(reportLoaded.Telegram.BotToken == FakeToken && !File.ReadAllText(reportFile).Contains(FakeToken), "report persistence preserves DPAPI token protection");
        File.WriteAllText(reportFile, WithReportFields(stored, null)); reportLoaded = AppSettings.Load(reportFile);
        Check(reportLoaded.Telegram.Enabled && reportLoaded.Telegram.StartNotificationsEnabled && reportLoaded.Telegram.ErrorNotificationsEnabled && reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 50 && reportLoaded.LoadWarning == null && reportLoaded.Telegram.BotToken == FakeToken,
            "legacy version-one profile defaults new report preferences without losing credentials");
        File.WriteAllText(reportFile, WithReportFields(stored, new Dictionary<string, object> { { "StartNotificationsEnabled", null }, { "ErrorNotificationsEnabled", null }, { "ProgressNotificationsEnabled", null }, { "ProgressEveryQueries", null } }));
        reportLoaded = AppSettings.Load(reportFile);
        Check(reportLoaded.Telegram.StartNotificationsEnabled && reportLoaded.Telegram.ErrorNotificationsEnabled && reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 50 && reportLoaded.LoadWarning == null,
            "nullable report fields retain backward-compatible defaults");
        File.WriteAllText(reportFile, WithReportFields(stored, new Dictionary<string, object> { { "StartNotificationsEnabled", "invalid" }, { "ErrorNotificationsEnabled", new object[0] }, { "ProgressNotificationsEnabled", new Dictionary<string, object>() }, { "ProgressEveryQueries", "50" } }));
        reportLoaded = AppSettings.Load(reportFile);
        Check(reportLoaded.Telegram.Enabled && reportLoaded.Telegram.BotToken == FakeToken && reportLoaded.Telegram.HasCredentials && !reportLoaded.Telegram.StartNotificationsEnabled && !reportLoaded.Telegram.ErrorNotificationsEnabled && !reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 50 && reportLoaded.LoadWarning != null,
            "malformed optional report types disable only affected reports and preserve credentials");
        foreach (object interval in new object[] { 0, -1, 1000001, 50.5m, "wrong", true, 2147483648L })
        {
            File.WriteAllText(reportFile, WithReportFields(stored, new Dictionary<string, object> { { "ProgressEveryQueries", interval } }));
            reportLoaded = AppSettings.Load(reportFile);
            Check(reportLoaded.Telegram.Enabled && reportLoaded.Telegram.StartNotificationsEnabled && reportLoaded.Telegram.ErrorNotificationsEnabled && !reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 50 && reportLoaded.Telegram.BotToken == FakeToken && reportLoaded.LoadWarning != null,
                "corrupt interval disables periodic reporting only");
        }
        reports.Telegram.ProgressEveryQueries = 0;
        bool refused = false;
        try { reports.Save(reportFile); } catch (ArgumentException) { refused = true; }
        Check(refused, "save rejects invalid active report interval");
        reports.Telegram.ProgressNotificationsEnabled = false; reports.Save(reportFile); reportLoaded = AppSettings.Load(reportFile);
        Check(!reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 50 && reportLoaded.Telegram.BotToken == FakeToken && reportLoaded.LoadWarning == null,
            "saving disabled periodic reporting normalizes inactive interval");
        reports.Telegram.Enabled = false; reports.Telegram.ProgressNotificationsEnabled = true; reports.Telegram.ProgressEveryQueries = 137;
        reports.Save(reportFile); reportLoaded = AppSettings.Load(reportFile);
        Check(!reportLoaded.Telegram.Enabled && !reportLoaded.Telegram.StartNotificationsEnabled && !reportLoaded.Telegram.ErrorNotificationsEnabled && reportLoaded.Telegram.ProgressNotificationsEnabled && reportLoaded.Telegram.ProgressEveryQueries == 137 && reportLoaded.Telegram.HasCredentials,
            "disabled master preserves chosen reports and credentials after restart");
        var multiTrainSettings = settings.Copy(); multiTrainSettings.Journey.TrainNumber = "00505, 541,505, 00561";
        Check(multiTrainSettings.Journey.Validate() == null, "CSV train selection validates without changing persisted string format");
        string multiTrainFile = Path.Combine(directory, "settings-multiple-trains.json"); multiTrainSettings.Save(multiTrainFile);
        var multipleLoaded = AppSettings.Load(multiTrainFile);
        Check(multipleLoaded.Journey.TrainNumber == multiTrainSettings.Journey.TrainNumber && multipleLoaded.Telegram.BotToken == FakeToken && multipleLoaded.LoadWarning == null,
            "CSV train selection and existing DPAPI credentials roundtrip");
        var loadedNumbers = TrainNumberSelection.Parse(multipleLoaded.Journey.TrainNumber);
        Check(loadedNumbers.Count == 3 && loadedNumbers[0] == 505 && loadedNumbers[1] == 541 && loadedNumbers[2] == 561,
            "reloaded CSV preserves normalized filter membership and input order");
        settings.Telegram.ChatId = "987654321"; settings.Save(file);
        Check(AppSettings.Load(file).Telegram.ChatId == "987654321", "atomic replacement saves updated setting");
        var success = new FakeHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":1}}");
        string result = await TelegramNotifier.SendAsync(Valid(), "열차 KTX 101 예매 버튼을 눌렀습니다.", success);
        Check(result == TelegramNotifier.SuccessMessage && success.Calls == 1, "sendMessage success once");
        Check(success.Method == "POST" && success.Host == "api.telegram.org" && success.Scheme == "https" && success.Path == "/bot" + FakeToken + "/sendMessage", "fixed HTTPS POST destination");
        Check(success.Body.Contains("chat_id=-1001234567890") && success.Body.Contains("text=") && !success.Body.Contains(FakeToken), "form body contains destination and message without token");
        var apiError = new FakeHandler(HttpStatusCode.OK, "{\"ok\":false,\"description\":\"" + FakeToken + "\"}");
        result = await TelegramNotifier.SendAsync(Valid(), "테스트", apiError);
        Check(result != TelegramNotifier.SuccessMessage && !result.Contains(FakeToken), "API refusal sanitized");
        var networkError = new FakeHandler(HttpStatusCode.OK, "") { ThrowNetwork = true };
        result = await TelegramNotifier.SendAsync(Valid(), "테스트", networkError);
        Check(result != TelegramNotifier.SuccessMessage && !result.Contains(FakeToken) && !result.Contains("https://"), "network exception sanitized");
        var redirect = new FakeHandler(HttpStatusCode.Redirect, "");
        result = await TelegramNotifier.SendAsync(Valid(), "테스트", redirect);
        Check(result.Contains("다른 주소") && redirect.Calls == 1, "redirect rejected without retry");
        var limited = new FakeHandler((HttpStatusCode)429, FakeToken);
        result = await TelegramNotifier.SendAsync(Valid(), "테스트", limited);
        Check(result.Contains("제한") && limited.Calls == 1 && !result.Contains(FakeToken), "rate limit reported without retry");
        var never = new FakeHandler(HttpStatusCode.OK, "{\"ok\":true}");
        var invalid = Valid(); invalid.BotToken = "invalid";
        result = await TelegramNotifier.SendAsync(invalid, "테스트", never);
        Check(never.Calls == 0 && result != TelegramNotifier.SuccessMessage, "invalid credentials fail before HTTP");
        invalid = Valid(); invalid.ChatId = "@someone";
        await TelegramNotifier.SendAsync(invalid, "테스트", never);
        Check(never.Calls == 0, "non-numeric chat ID rejected");
        invalid = Valid(); invalid.Enabled = false;
        await TelegramNotifier.SendAsync(invalid, "테스트", never);
        Check(never.Calls == 0, "disabled Telegram causes no HTTP");
        File.WriteAllText(file, "{\"Version\":1,\"Journey\":null,\"Telegram\":{\"Enabled\":true,\"ChatId\":\"123\",\"BotTokenProtected\":\"bad\"}}");
        loaded = AppSettings.Load(file);
        Check(!loaded.Telegram.Enabled && loaded.Telegram.BotToken == "" && loaded.LoadWarning != null, "unreadable token disables notification safely");
        Check(!loaded.Telegram.HasCredentials, "unreadable saved token reopens first-run setup");
        var chats = new FakeHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":[{\"update_id\":1,\"message\":{\"chat\":{\"id\":123,\"type\":\"private\",\"first_name\":\"Kim\",\"username\":\"kim\"}}},{\"update_id\":2,\"message\":{\"chat\":{\"id\":123,\"type\":\"private\",\"first_name\":\"Kim\"}}},{\"update_id\":3,\"message\":{\"chat\":{\"id\":-100123,\"type\":\"supergroup\",\"title\":\"Excluded group\"}}},{\"update_id\":4,\"edited_message\":{\"chat\":{\"id\":456,\"type\":\"private\",\"first_name\":\"Lee\"}}}]}");
        TelegramChatLookup found = await TelegramNotifier.FindChatsAsync(FakeToken, chats);
        Check(found.Error == null && found.Chats.Count == 2 && found.Chats[0].ChatId == "123" && found.Chats[1].ChatId == "456", "private chat lookup deduplicates and excludes groups");
        Check(chats.Calls == 1 && chats.Path.EndsWith("/getUpdates") && chats.Method == "POST" && chats.Body.Contains("limit=100") && chats.Body.Contains("timeout=0") && !chats.Body.Contains("offset") && !chats.Body.Contains("allowed_updates"), "lookup does not consume updates or reconfigure bot");
        var empty = new FakeHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":[]}");
        found = await TelegramNotifier.FindChatsAsync(FakeToken, empty);
        Check(found.Chats.Count == 0 && found.Error.Contains("/start"), "empty lookup gives start instruction");
        var lookupError = new FakeHandler(HttpStatusCode.OK, "{\"ok\":false,\"description\":\"" + FakeToken + "\"}");
        found = await TelegramNotifier.FindChatsAsync(FakeToken, lookupError);
        Check(found.Error != null && !found.Error.Contains(FakeToken), "lookup API error sanitized");
        var webhook = new FakeHandler(HttpStatusCode.Conflict, FakeToken);
        found = await TelegramNotifier.FindChatsAsync(FakeToken, webhook);
        Check(found.Error.Contains("웹훅") && found.Error.Contains("직접") && webhook.Calls == 1 && !found.Error.Contains(FakeToken), "webhook conflict preserves existing bot configuration");
        var lookupNetwork = new FakeHandler(HttpStatusCode.OK, "") { ThrowNetwork = true };
        found = await TelegramNotifier.FindChatsAsync(FakeToken, lookupNetwork);
        Check(found.Error != null && !found.Error.Contains(FakeToken) && !found.Error.Contains("https://"), "lookup network exception sanitized");
        var badToken = new FakeHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":[]}");
        found = await TelegramNotifier.FindChatsAsync("bad", badToken);
        Check(found.Error != null && badToken.Calls == 0, "lookup invalid token fails before network");
    }
    sealed class FakeHandler : HttpMessageHandler
    {
        readonly HttpStatusCode status; readonly string body;
        public int Calls; public bool ThrowNetwork; public string Method, Host, Scheme, Path, Body;
        public FakeHandler(HttpStatusCode statusCode, string responseBody) { status = statusCode; body = responseBody; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Method = request.Method.Method; Host = request.RequestUri.Host; Scheme = request.RequestUri.Scheme; Path = request.RequestUri.AbsolutePath;
            Body = await request.Content.ReadAsStringAsync();
            if (ThrowNetwork) throw new HttpRequestException("Synthetic network failure https://api.telegram.org/bot" + FakeToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
