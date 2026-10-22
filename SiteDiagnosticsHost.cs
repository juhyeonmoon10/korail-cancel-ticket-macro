using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    readonly SiteDiagnostics siteDiagnostics = new SiteDiagnostics();
    Button bookingButton, refreshButton;

    static bool OfficialTicketSource(string source)
    {
        Uri uri;
        return Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
            (uri.Host == "www.korail.com" || uri.Host == "korail.com") && uri.AbsolutePath.StartsWith("/ticket/", StringComparison.Ordinal);
    }

    string SiteBlockMessage()
    {
        return "코레일 자동화 차단 확인" + (String.IsNullOrEmpty(siteDiagnostics.BlockCode) ? "" : " (" + siteDiagnostics.BlockCode + ")") +
            " · 이 실행의 자동 기능을 중지했습니다. ‘브라우저 진단 저장’으로 기록할 수 있습니다.";
    }

    bool AutomationAvailable()
    {
        if (Program.ManualDiagnostic) { status.Text = "수동 진단 모드에서는 자동 입력과 도우미 스크립트를 사용하지 않습니다."; return false; }
        if (siteDiagnostics.SiteBlocked || mouseRefreshSiteBlocked) { status.Text = SiteBlockMessage(); return false; }
        return true;
    }

    void ObserveSiteBlock(string evidence)
    {
        bool alreadyBlocked = siteDiagnostics.SiteBlocked;
        if (!siteDiagnostics.TryBlock(evidence)) return;
        mouseRefreshSiteBlocked = true;
        bookingButton.Enabled = refreshButton.Enabled = false;
        activeDocument = false; lease.Reset();
        StopBookingQueue(SiteBlockMessage());
        if (!alreadyBlocked) StopMouseBooking(SiteBlockMessage());
        status.Text = SiteBlockMessage();
    }

    void SaveBrowserDiagnostics()
    {
        try
        {
            string directory = Path.Combine(Program.DataRoot, "diagnostics");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "browser-latest.json");
            File.WriteAllText(file, json.Serialize(new {
                appVersion = "0.7.2", savedAtUtc = DateTime.UtcNow.ToString("o"),
                blockObservationEnabled = !Program.ManualDiagnostic, inputActionMeaning = "attempt",
                diagnosis = siteDiagnostics.Snapshot(),
                notice = "이 기록은 차단 원인을 확정하지 않습니다. 수동 진단에서는 차단 감지도 꺼져 있으므로 화면 결과를 직접 기록하세요. 입력 기록은 시도이며 성공 증명이 아닙니다. 웹 요청 본문·쿠키·토큰·전체 URL은 저장하지 않습니다."
            }), new UTF8Encoding(false));
            status.Text = "브라우저 진단 저장: " + file;
        }
        catch { status.Text = "브라우저 진단을 저장하지 못했습니다. 저장 폴더를 확인하세요."; }
    }
}
