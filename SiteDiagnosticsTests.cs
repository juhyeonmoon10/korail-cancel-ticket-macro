using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal static class SiteDiagnosticsTests
{
    static int count;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        count++;
    }
    static Dictionary<string, object> Snapshot(SiteDiagnostics model)
    { return (Dictionary<string, object>)model.Snapshot(); }
    static Dictionary<string, object>[] Events(SiteDiagnostics model)
    { return (Dictionary<string, object>[])Snapshot(model)["events"]; }
    static string Dump(SiteDiagnostics model)
    { return new JavaScriptSerializer().Serialize(model.Snapshot()); }

    static int Main()
    {
        try
        {
            var model = new SiteDiagnostics();
            Check(!model.SiteBlocked && model.BlockCode == "" && Events(model).Length == 0, "empty default");
            model.WebViewRuntime = "143.0.3650.12"; model.SdkVersion = "1.0.4258.31"; model.ManualMode = true;
            Check(model.WebViewRuntime == "143.0.3650.12" && model.SdkVersion == "1.0.4258.31" && model.ManualMode, "versions and mode");
            model.WebViewRuntime = "143.0.0.1 secret"; model.SdkVersion = "1.0.0.0\n";
            Check(model.WebViewRuntime == "" && model.SdkVersion == "", "version rejects suffix and newline");
            model.WebViewRuntime = new String('1', 65); model.SdkVersion = "1.2.3.4.5.6.7";
            Check(model.WebViewRuntime == "" && model.SdkVersion == "", "version bounds");

            Check(!model.TryBlock(null) && !model.TryBlock("") && !model.TryBlock("CODE : -1234"), "unrelated code not blocked");
            Check(!model.TryBlock("-80020") && !model.TryBlock("CODE : -80020") && !model.TryBlock("value -8002 text"), "exact code boundary");
            Check(!model.SiteBlocked, "no false latch");
            Check(model.TryBlock(" -8002 ") && model.SiteBlocked && model.BlockCode == "-8002", "known code");
            Check(model.TryBlock("CODE : -8002") && Events(model).Length == 1, "known label and duplicate bounded event");
            Check(!model.TryBlock("CODE : -1234") && model.SiteBlocked && model.BlockCode == "-8002", "latch survives unrelated message");
            model.AddNavigation("https://www.korail.com/ticket/main", true, "200");
            model.ManualMode = false; model.AddAction("helper-mode");
            model.ManualMode = true; model.AddAction("manual-mode");
            Check(model.SiteBlocked && model.BlockCode == "-8002", "navigation and mode changes never reset block latch");

            var phrase = new SiteDiagnostics();
            Check(phrase.TryBlock("자동화된 요청으로 감지되어 차단되었습니다.") && phrase.SiteBlocked && phrase.BlockCode == "", "phrase without invented code");
            Check(phrase.TryBlock("자동화된\n요청으로 감지되어\t차단되었습니다. CODE : -8123") && phrase.BlockCode == "-8123", "explicit phrase optional other code");
            Check(Events(phrase).Length == 2, "later observed code adds event");
            Check(phrase.TryBlock("-8002") && phrase.BlockCode == "-8123", "retain first observed block code");
            var longInput = new SiteDiagnostics();
            Check(!longInput.TryBlock(new String('x', 8193) + "-8002") && !longInput.SiteBlocked, "message size bounded");
            var longCode = new SiteDiagnostics();
            Check(longCode.TryBlock("자동화된 요청으로 감지되어 차단되었습니다. CODE : -1234567") && longCode.BlockCode == "", "overlong code not partially retained");

            var navigation = new SiteDiagnostics();
            navigation.AddNavigation("https://www.korail.com/ticket/search/list?token=PRIVATE_SENTINEL", true, "200");
            Check((string)Events(navigation)[0]["category"] == "korail-ticket" && (string)Events(navigation)[0]["status"] == "200", "official category and http status");
            navigation.AddNavigation("https://korail-helper.invalid/card-demo.html?case=PRIVATE_SENTINEL", true, "Unknown");
            Check((string)Events(navigation)[1]["category"] == "local-demo", "local category");
            navigation.AddNavigation("https://korail.com.evil.example/ticket/search/list", false, "CertificateExpired");
            Check((string)Events(navigation)[2]["category"] == "other" && (string)Events(navigation)[2]["status"] == "CertificateExpired", "host boundary and enum status");
            navigation.AddNavigation("http://korail.com/ticket/search/list", false, "PRIVATE_SENTINEL");
            Check((string)Events(navigation)[3]["category"] == "other" && (string)Events(navigation)[3]["status"] == "unrecognized", "scheme and arbitrary status rejected");
            navigation.AddNavigation("https://www.korail.com:8443/ticket/search/list", false, "600");
            Check((string)Events(navigation)[4]["category"] == "other" && (string)Events(navigation)[4]["status"] == "unrecognized", "alternate port and invalid http status rejected");
            navigation.AddNavigation("https://www.korail.com/ticketish/secret", false, "Timeout");
            navigation.AddNavigation("https://korail-helper.invalid/welcome.html", false, "Unknown");
            navigation.AddNavigation("not a url PRIVATE_SENTINEL", false, "Unknown");
            Check((string)Events(navigation)[5]["category"] == "other" && (string)Events(navigation)[6]["category"] == "other" && (string)Events(navigation)[7]["category"] == "other", "page boundary and malformed uri");
            navigation.AddAction("native-query"); navigation.AddAction("PRIVATE_SENTINEL"); navigation.AddAction(null);
            Check(Events(navigation).Length == 9 && (string)Events(navigation)[8]["action"] == "native-query", "action allowlist");
            navigation.AddAction("native-notice");
            Check(Events(navigation).Length == 10 && (string)Events(navigation)[9]["action"] == "native-notice", "notice input has distinct action label");
            Check(!Dump(navigation).Contains("PRIVATE_SENTINEL") && !Dump(navigation).Contains("https://"), "snapshot contains no raw url or free text");
            var copy = Events(navigation); copy[0]["category"] = "PRIVATE_SENTINEL";
            Check((string)Events(navigation)[0]["category"] == "korail-ticket", "snapshot detached from storage");

            var bounded = new SiteDiagnostics(); bounded.AddAction("start");
            for (int i = 0; i < 105; i++) bounded.AddAction("native-query");
            Check(Events(bounded).Length == 100 && (string)Events(bounded)[0]["action"] == "native-query", "fifo keeps last 100 events");
            DateTime parsed;
            Check(DateTime.TryParse((string)Events(bounded)[0]["atUtc"], out parsed), "timestamp serializable");
            Check(Dump(bounded).Length < 20000, "bounded serialized payload");
            Console.WriteLine("SiteDiagnostics: " + count + " checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
