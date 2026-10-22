using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static class SettingsTests
{
    const string FakeToken = "123456789:THIS_IS_A_SYNTHETIC_TEST_TOKEN_12345";
    static int count;
    static int Main(string[] args)
    {
        try { Run(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "korail-settings-tests") : args[0]).GetAwaiter().GetResult(); Console.WriteLine("PASS " + count + " settings and Telegram checks (mock HTTP only)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message.Replace(FakeToken, "[redacted]")); return 1; }
    }
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); count++; Console.WriteLine("PASS " + name); }
    static TelegramSettings Valid() { return new TelegramSettings { Enabled = true, BotToken = FakeToken, ChatId = "-1001234567890" }; }
    static async Task Run(string directory)
    {
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
