using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

[assembly: AssemblyTitle("코레일 한 장 도우미 PC")]
[assembly: AssemblyDescription("개인용 열차 예매 보조 도구 - 비공식")]
[assembly: AssemblyVersion("0.9.1.0")]
[assembly: AssemblyFileVersion("0.9.1.0")]

internal static class Program
{
    public static string DataRoot, BundleRoot, TestOutput;
    public static bool Demo, SelfTest, ManualDiagnostic;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ManualDiagnostic = args.Contains("--manual-diagnostic");
        SelfTest = !ManualDiagnostic && args.Contains("--self-test");
        Demo = !ManualDiagnostic && args.Contains("--demo");
        TestOutput = Value(args, "--result");
        DataRoot = Value(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KorailOneHelper");
        DataRoot = Path.GetFullPath(DataRoot);
        bool created;
        string sid = WindowsIdentity.GetCurrent().User.Value;
        using (var mutex = new Mutex(true, "Local\\KorailOneHelper_PC_" + sid + (SelfTest ? "_test" : ""), out created))
        {
            if (!created) { MessageBox.Show("이미 실행 중입니다. 열린 코레일 도우미 창을 사용하세요.", "코레일 한 장 도우미"); return 2; }
            try
            {
                BundleRoot = Path.Combine(DataRoot, "app-0.9.1");
                Extract();
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
                {
                    string name = new AssemblyName(eventArgs.Name).Name;
                    if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
                    return Assembly.LoadFrom(Path.Combine(BundleRoot, name + ".dll"));
                };
                if (LoadLibraryEx(Path.Combine(BundleRoot, "WebView2Loader.dll"), IntPtr.Zero, 8) == IntPtr.Zero)
                    throw new InvalidOperationException("WebView2 로더를 불러오지 못했습니다. 오류 " + Marshal.GetLastWin32Error());
                Launch();
                return Environment.ExitCode;
            }
            catch (Exception ex)
            {
                if (SelfTest && TestOutput != null) File.WriteAllText(TestOutput, ex.ToString());
                else MessageBox.Show("시작하지 못했습니다.\n\n" + ex.Message, "코레일 한 장 도우미", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Launch() { Application.Run(new MainWindow()); }
    static string Value(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    static void Extract()
    {
        Directory.CreateDirectory(BundleRoot);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("bundle.")))
        {
            string file = Path.Combine(BundleRoot, name.Substring(7));
            using (Stream stream = assembly.GetManifestResourceStream(name))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory); byte[] bytes = memory.ToArray();
                bool same = false;
                if (File.Exists(file)) using (var sha = SHA256.Create())
                    same = sha.ComputeHash(File.ReadAllBytes(file)).SequenceEqual(sha.ComputeHash(bytes));
                if (!same) File.WriteAllBytes(file, bytes);
            }
        }
    }
}

internal sealed class LeaseGate
{
    string owner;
    DateTime expires;
    public bool Request(string type, string token, bool active)
    {
        if (type == "release") { if (owner == token) owner = null; return true; }
        if (!active || String.IsNullOrEmpty(token) || token.Length > 100) return false;
        DateTime now = DateTime.UtcNow;
        if (type == "check" && (owner != token || expires <= now)) return false;
        if (type == "acquire" && owner != null && owner != token && expires > now) return false;
        if (type != "check" && type != "acquire") return false;
        owner = token; expires = now.AddMinutes(5); return true;
    }
    public void Reset() { owner = null; }
}

internal sealed partial class MainWindow : Form
{
    const string LocalBase = "https://korail-helper.invalid/";
    readonly WebView2 view = new WebView2();
    readonly Label status = new Label();
    readonly Label notificationStatus = new Label();
    readonly TextBox address = new TextBox();
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly LeaseGate lease = new LeaseGate();
    readonly List<string> testResults = new List<string>();
    readonly NotificationInbox notifications = new NotificationInbox();
    readonly List<ClickNotice> demoNotifications = new List<ClickNotice>();
    AppSettings settings;
    string nonce = Guid.NewGuid().ToString("N");
    string observedSource;
    int navigation;
    ulong navigationId;
    bool activeDocument, ready, closing, navigationPending;
    TaskCompletionSource<bool> loadWaiter;
    public MainWindow()
    {
        Text = "코레일 한 장 도우미 PC 0.9.1 · " + (Program.ManualDiagnostic ? "수동 진단 · 자동 기능 꺼짐" : "비공식");
        settings = AppSettings.Load(Path.Combine(Program.DataRoot, "settings.json"));
        ClientSize = new Size(1320, 900); MinimumSize = new Size(1000, 760);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("맑은 고딕", 10);
        InitializeDesktopShell();
        LoadSelectionMemory();
        KeyPreview = true;
        KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.F8) { e.Handled = true; await StopHelper("F8로 중지했습니다."); } };
        Resize += async delegate {
            if (WindowState == FormWindowState.Minimized)
            {
                StopBookingQueue("창 최소화로 예약 순서를 중지했습니다.");
                if (CanPauseMouseBooking()) InterruptMouseBooking("창 최소화");
                else await StopHelper("최소화하여 중지했습니다. 창을 연 뒤 다시 시작하세요.");
            }
            else if (ready && !closing) activeDocument = !Program.ManualDiagnostic && !siteDiagnostics.SiteBlocked && HelperPage(view.CoreWebView2.Source);
        };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        FormClosing += delegate { StopBookingQueue("프로그램을 닫아 예약 순서를 중지했습니다."); StopMouseBooking(null); closing = true; activeDocument = false; lease.Reset(); SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged; };
        FormClosed += delegate { ReleaseSleepPrevention(); view.Dispose(); };
        Shown += async delegate {
            if (!Program.SelfTest && !Program.Demo && !Program.ManualDiagnostic && !settings.Telegram.HasCredentials)
                await EditSettings(true, true);
            if (!closing) await Initialize();
        };
        if (Program.SelfTest) { ShowInTaskbar = false; Opacity = 0; }
    }
    async Task EditSettings(bool openTelegram, bool firstRun = false)
    {
        if (ready) await StopHelper("설정을 변경하기 위해 중지했습니다.");
        string previousJourney = json.Serialize(settings.Journey);
        using (var dialog = new SettingsDialog(settings, openTelegram, firstRun))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                dialog.Result.Save(Path.Combine(Program.DataRoot, "settings.json"));
                settings = dialog.Result;
                notificationStatus.Text = settings.Telegram.Enabled ? "텔레그램 알림 켜짐 · 예약 완료 확인 후 전송" : "텔레그램 알림 꺼짐";
                // Telegram changes do not affect the query or the selected trains.
                // Only a changed journey filter requires a fresh helper instance.
                if (ready && HelperPage(view.CoreWebView2.Source) && previousJourney != json.Serialize(settings.Journey)) view.Reload();
            }
            catch { MessageBox.Show("설정을 저장하지 못했습니다. 저장 폴더를 확인하세요.", "저장 실패"); }
        }
    }
    async Task SaveDiagnostics()
    {
        if (!ready || closing) return;
        if (Program.ManualDiagnostic) { SaveBrowserDiagnostics(); return; }
        try
        {
            // Read-only structural export of the visible booking area. Never read browser storage or credentials.
            string result = await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              if(!((location.protocol==='https:' && ['www.korail.com','korail.com'].includes(location.hostname) && ['/ticket/search/list','/ticket/reservation/detail','/ticket/reservation/list'].includes(location.pathname)) || (location.hostname==='korail-helper.invalid' && ['/demo.html','/card-demo.html'].includes(location.pathname)))) return {found:false};
              const root=document.querySelector('#container') || document.querySelector('main');
              if(!root) return {found:false,error:'진단할 조회 영역을 찾지 못했습니다.'};
              const clone=root.cloneNode(true);
              const tripFields=new Set(['labelstart','labelend','startDate','labelple']);
              clone.querySelectorAll('script,style,iframe,object,embed,svg,canvas,input[type=password],input[type=hidden]').forEach(n=>n.remove());
              for(const n of [clone,...clone.querySelectorAll('*')]) {
                for(const a of [...n.attributes]) {
                  if(/^on/i.test(a.name) || /token|secret|password|passwd|authorization|session|cookie/i.test(a.name) || a.name==='srcdoc') n.removeAttribute(a.name);
                }
                if(n.matches('input,textarea')) {
                  n.removeAttribute('value');n.removeAttribute('checked');if(n.tagName==='TEXTAREA')n.textContent='';
                  if(tripFields.has(n.id)) {const original=document.getElementById(n.id);if(original && original.getClientRects().length && typeof original.value==='string')n.setAttribute('value',original.value.slice(0,200));}
                }
                for(const key of ['href','src','action','formaction']) if(n.hasAttribute(key)) {
                  try {const u=new URL(n.getAttribute(key),location.href);n.setAttribute(key,/^https?:$/.test(u.protocol)?u.origin+u.pathname:'');}
                  catch {n.removeAttribute(key);}
                }
              }
              const html=clone.outerHTML;
              return {found:true,source:location.origin+location.pathname,title:document.title.slice(0,200),selector:root.id===""container""?'#container':'main',truncated:html.length>1000000,html:html.slice(0,1000000)};
            })()");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4194304 };
            var data = serializer.Deserialize<Dictionary<string, object>>(result);
            object found;
            if (data == null || !data.TryGetValue("found", out found) || !(found is bool) || !(bool)found)
            { status.Text = "진단 영역을 찾지 못했습니다. 열차 조회 결과 또는 예약 상세 화면에서 다시 저장하세요."; return; }
            data["savedAtUtc"] = DateTime.UtcNow.ToString("o");
            string directory = Path.Combine(Program.DataRoot, "diagnostics");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "latest.json");
            File.WriteAllText(file, serializer.Serialize(data), new System.Text.UTF8Encoding(false));
            status.Text = "화면 진단 저장: " + file;
        }
        catch { status.Text = "화면 진단을 저장하지 못했습니다. 조회 결과 화면과 저장 폴더를 확인하세요."; }
    }
    async Task Initialize()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataRoot, "browser"), null);
            await view.EnsureCoreWebView2Async(env);
            siteDiagnostics.WebViewRuntime = env.BrowserVersionString;
            siteDiagnostics.SdkVersion = typeof(CoreWebView2).Assembly.GetName().Version.ToString();
            siteDiagnostics.AddAction(Program.ManualDiagnostic ? "mode-manual" : "mode-helper");
            siteDiagnostics.AddAction("browser-start");
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("korail-helper.invalid", Program.BundleRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            view.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            view.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            // Handle all script dialogs visibly; only recognized stop alerts may use native Enter.
            view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = Program.ManualDiagnostic;
            view.CoreWebView2.NavigationStarting += NavigationStarting;
            view.CoreWebView2.NavigationCompleted += NavigationCompleted;
            view.CoreWebView2.SourceChanged += SourceChanged;
            if (!Program.ManualDiagnostic)
            {
                view.CoreWebView2.WebMessageReceived += MessageReceived;
                view.CoreWebView2.ScriptDialogOpening += MouseScriptDialog;
            }
            view.CoreWebView2.NewWindowRequested += async delegate { await StopHelper("새 창이 열려 중지했습니다. 로그인·예약·결제 결과를 직접 확인하세요."); };
            view.CoreWebView2.ProcessFailed += delegate { StopBookingQueue("브라우저가 종료되어 예약 순서를 중지했습니다."); StopMouseBooking(null); activeDocument = false; lease.Reset(); status.Text = "브라우저가 종료됐습니다. 앱을 다시 실행하세요."; };
            ready = true;
            if (Program.SelfTest) await RunTests();
            else Navigate(LocalBase + (Program.Demo ? "demo.html" : "welcome.html"));
        }
        catch (Exception ex)
        {
            if (Program.SelfTest) { FinishTests(ex); return; }
            status.Text = "시작 실패: " + ex.Message;
            MessageBox.Show("브라우저를 시작할 수 없습니다.\nMicrosoft Edge WebView2 Runtime 설치 여부를 확인하세요.\n\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message, "시작 실패");
        }
    }
    void Navigate(string url) { if (ready && !closing) view.CoreWebView2.Navigate(url); }
    static bool DifferentPagePath(string previous, string current)
    {
        Uri before, after;
        return Uri.TryCreate(previous, UriKind.Absolute, out before) && Uri.TryCreate(current, UriKind.Absolute, out after)
            && !String.Equals(before.GetLeftPart(UriPartial.Path), after.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);
    }
    async void SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
    {
        string source = view.CoreWebView2.Source, previous = observedSource;
        observedSource = source; address.Text = source;
        if (Program.ManualDiagnostic) { activeDocument = false; return; }
        if (e.IsNewDocument || !DifferentPagePath(previous, source))
        {
            if (activeDocument && HelperPage(source)) RegisterNotificationPage();
            return;
        }
        // SPA route changes keep the JavaScript world alive. Clear closures holding old buttons/seats.
        activeDocument = false; lease.Reset();
        if (mouseBookingActive && !mouseSubmitted) StopMouseBooking("페이지 경로가 바뀌어 마우스 자동 진행을 중지했습니다.");
        int page = navigation;
        const string reason = "화면이 바뀌어 진행을 멈췄습니다. 저장한 열차는 같은 조회 결과에서 복원됩니다.";
        try
        {
            bool reset = await EvalBool("window.KorailDesktopHelper && typeof window.KorailDesktopHelper.reset === 'function' && window.KorailDesktopHelper.reset(" + json.Serialize(reason) + ")");
            if (closing || page != navigation || view.CoreWebView2.Source != source) return;
            activeDocument = reset && !siteDiagnostics.SiteBlocked && HelperPage(source) && WindowState != FormWindowState.Minimized;
            if (reset && HelperPage(source))
            {
                RegisterNotificationPage();
                if (!Program.SelfTest) await RestoreSavedSelection();
                if (closing || page != navigation || view.CoreWebView2.Source != source) return;
            }
            status.Text = siteDiagnostics.SiteBlocked ? SiteBlockMessage() : reason;
        }
        catch { if (!closing && page == navigation) status.Text = "화면이 바뀌어 도우미를 중지했습니다. 저장한 열차는 유지되며 조회 결과에서 다시 확인할 수 있습니다."; }
    }
    void NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (mouseBookingActive && mouseSubmitted && !MouseAllowedSource(e.Uri))
            StopMouseBooking("예약을 확인할 공식 페이지를 벗어나 중지했습니다. 직접 결과를 확인하세요.");
        Uri uri;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || (uri.Scheme != "https" && e.Uri != "about:blank"))
        { e.Cancel = true; status.Text = "HTTPS 페이지를 앱 안에서 이용하세요."; return; }
        notifications.Retire(nonce);
        if (mouseBookingActive && !mouseSubmitted) StopMouseBooking("페이지가 이동하여 마우스 자동 진행을 중지했습니다.");
        navigationPending = true;
        navigation++; navigationId = e.NavigationId; nonce = Guid.NewGuid().ToString("N"); activeDocument = false; lease.Reset();
        status.Text = "페이지 이동 중 · 이전 조회는 중지됐습니다.";
    }
    static bool HelperPage(string source)
    {
        Uri uri; if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || uri.Scheme != "https") return false;
        if (uri.Host == "korail-helper.invalid") return uri.AbsolutePath == "/demo.html" || uri.AbsolutePath == "/card-demo.html";
        return ((uri.Host == "www.korail.com" || uri.Host == "korail.com") && uri.AbsolutePath.StartsWith("/ticket/")) || uri.Host == "www.letskorail.com";
    }
    async void NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId != navigationId) return;
        navigationPending = false;
        if (!e.IsSuccess && mouseBookingActive && mouseSubmitted)
            StopMouseBooking("예약 결과 페이지를 열지 못했습니다. 예매를 다시 누르지 말고 공식 예약내역을 확인하세요.");
        int page = navigation;
        try
        {
            siteDiagnostics.AddNavigation(view.CoreWebView2.Source, e.IsSuccess, e.WebErrorStatus.ToString());
            if (!e.IsSuccess) throw new InvalidOperationException("페이지를 열지 못했습니다: " + e.WebErrorStatus);
            if (Program.ManualDiagnostic)
            {
                activeDocument = false;
                status.Text = "수동 진단 · 도우미 스크립트와 자동 입력 없음 · 직접 한 번 조회한 결과를 비교하세요.";
            }
            else if (HelperPage(view.CoreWebView2.Source))
            {
                activeDocument = !siteDiagnostics.SiteBlocked;
                RegisterNotificationPage();
                string retained = Program.SelfTest || view.CoreWebView2.Source.StartsWith(LocalBase) ? "" : " const desktopSelection = " + json.Serialize(savedSelection == null ? null : savedSelection.ForPage()) + ";\n";
                string script = "(() => { const desktopNativeMore = " + (Program.SelfTest ? "false" : "true") + "; const desktopNonce = " + json.Serialize(nonce) + "; const desktopJourney = " + json.Serialize(settings.Journey) + ";\n" + retained + File.ReadAllText(Path.Combine(Program.BundleRoot, "core.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "korail-site.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "site-block.js")) + "\n" + File.ReadAllText(Path.Combine(Program.BundleRoot, "content.js")) + "\n})();";
                string result = await view.CoreWebView2.ExecuteScriptAsync(script);
                if (page != navigation) return;
                bool injected = await EvalBool("!!window.KorailDesktopHelper");
                if (!injected) throw new InvalidOperationException("도우미 초기화 실패. 연습 화면에서 다시 확인하세요.");
                siteDiagnostics.AddAction("helper-initialized");
                status.Text = siteDiagnostics.SiteBlocked ? SiteBlockMessage() : view.CoreWebView2.Source.StartsWith(LocalBase) ? "모의 연습 · 실제 코레일 요청/예약 없음" : "코레일 · 직접 조회한 뒤 도우미에서 열차 목록을 읽고 원하는 열차를 선택하세요.";
            }
            else status.Text = "사용 안내 · 코레일 열기 또는 연습 화면을 선택하세요.";
            if (page == navigation && loadWaiter != null) loadWaiter.TrySetResult(true);
        }
        catch (Exception ex)
        {
            activeDocument = false; lease.Reset(); status.Text = ex.Message;
            if (loadWaiter != null) loadWaiter.TrySetException(ex);
        }
    }
    async void MessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (closing || Program.ManualDiagnostic || !HelperPage(e.Source)) return;
        try
        {
            string raw = e.WebMessageAsJson; if (raw.Length > 32768) return;
            var data = json.Deserialize<Dictionary<string, object>>(raw);
            string kind = Get(data, "kind");
            if (kind == "reservationClick")
            {
                ClickNotice notice;
                bool accepted = notifications.Handle(e.Source, data, out notice);
                if (Get(data, "phase") == "prepare" && Get(data, "nonce") == nonce && e.Source == view.CoreWebView2.Source)
                    view.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { kind = "reservationClickResult", id = Get(data, "id"), ok = accepted, nonce = nonce }));
                // Production messages are sent only after the separate native workflow confirms a receipt.
                if (notice != null && notice.Demo) DeliverNotification(notice);
                return;
            }
            if (Get(data, "nonce") != nonce || e.Source != view.CoreWebView2.Source) return;
            if (kind == "selectionSaved" || kind == "selectionCleared")
            {
                if (Program.SelfTest || !activeDocument || queueRunning || !OfficialTicketSource(e.Source)) return;
                if (kind == "selectionSaved")
                {
                    Uri selectionSource;
                    if (!Uri.TryCreate(e.Source, UriKind.Absolute, out selectionSource) || selectionSource.AbsolutePath != "/ticket/search/list") return;
                    ReceiveSelection(data);
                }
                else
                {
                    if (File.Exists(SelectionFile)) File.Delete(SelectionFile);
                    savedSelection = null;
                }
                return;
            }
            if (kind == "siteBlocked")
            {
                if (OfficialTicketSource(e.Source)) ObserveSiteBlock(Get(data, "code"));
                return;
            }
            if (kind == "readAllTrains")
            {
                if (activeDocument && !Program.SelfTest) await StartMouseReadAll();
                else FinishTrainReadRequest();
                return;
            }
            if (kind == "lease")
            {
                bool allowed = activeDocument && !siteDiagnostics.SiteBlocked && WindowState != FormWindowState.Minimized && Visible;
                bool ok = lease.Request(Get(data, "type"), Get(data, "token"), allowed);
                view.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { kind = "leaseResult", id = Get(data, "id"), ok = ok, nonce = nonce }));
            }
            else if (kind == "status")
            {
                string text = Get(data, "text"); if (text.Length > 500) text = text.Substring(0, 500);
                if (!mouseBookingActive) status.Text = siteDiagnostics.SiteBlocked ? SiteBlockMessage() : (e.Source.StartsWith(LocalBase) ? "[모의 연습] " : "[코레일] ") + text.Replace('\n', ' ');
            }
        }
        catch { /* Ignore malformed page messages. No credentials or arbitrary endpoints are exposed. */ }
    }
    void RegisterNotificationPage()
    { notifications.Register(nonce, view.CoreWebView2.Source, settings.Journey, Program.SelfTest || view.CoreWebView2.Source.StartsWith(LocalBase)); }
    async void DeliverNotification(ClickNotice notice)
    {
        string result;
        if (notice.Demo)
        {
            demoNotifications.Add(notice);
            result = "모의 클릭 알림 감지 · 텔레그램에는 전송하지 않았습니다.";
        }
        else
        {
            notificationStatus.Text = settings.Telegram.Enabled ? "예매 클릭 감지 · 텔레그램 전송 중…" : "예매 클릭 감지 · 텔레그램 설정이 꺼져 있습니다.";
            result = await TelegramNotifier.SendAsync(settings.Telegram, notice.Message());
        }
        if (closing || IsDisposed) return;
        notificationStatus.Text = result;
        if (notice.Nonce == nonce && ready)
            view.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { kind = "notificationStatus", text = result, nonce = nonce }));
    }
    static string Get(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : ""; }
    async Task StopHelper(string reason, bool preserveQueue = false)
    {
        if (!preserveQueue) StopBookingQueue(reason);
        StopMouseBooking(reason);
        int stoppedGeneration = mouseGeneration;
        activeDocument = false; lease.Reset(); int page = navigation;
        if (ready && !closing && !Program.ManualDiagnostic) try
        {
            await view.CoreWebView2.ExecuteScriptAsync("window.KorailDesktopHelper && window.KorailDesktopHelper.stop(" + json.Serialize(reason) + ")");
        }
        catch { }
        if (ready && !closing && page == navigation) activeDocument = !Program.ManualDiagnostic && !siteDiagnostics.SiteBlocked && HelperPage(view.CoreWebView2.Source) && WindowState != FormWindowState.Minimized;
        if (!closing && stoppedGeneration == mouseGeneration) status.Text = siteDiagnostics.SiteBlocked ? SiteBlockMessage() : reason;
    }
    void PowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Suspend && !closing) BeginInvoke(new Action(async delegate { await StopHelper("절전 진입으로 중지했습니다."); })); }
    void SessionChanged(object sender, SessionSwitchEventArgs e)
    { if (e.Reason == SessionSwitchReason.SessionLock && !closing) BeginInvoke(new Action(async delegate { await StopHelper("화면 잠금으로 중지했습니다."); })); }
    async Task<bool> EvalBool(string expression)
    { return (await view.CoreWebView2.ExecuteScriptAsync("Boolean(" + expression + ")")) == "true"; }
    async Task<string> EvalString(string expression)
    { return json.Deserialize<string>(await view.CoreWebView2.ExecuteScriptAsync("String(" + expression + ")")); }
    async Task LoadDemo(string scenario)
    {
        loadWaiter = new TaskCompletionSource<bool>();
        Navigate(LocalBase + "demo.html?test=" + Guid.NewGuid().ToString("N"));
        var done = await Task.WhenAny(loadWaiter.Task, Task.Delay(15000));
        if (done != loadWaiter.Task) throw new TimeoutException("모의 화면 로딩 제한시간 초과");
        await loadWaiter.Task; loadWaiter = null;
        await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('scenario').value=" + json.Serialize(scenario) + ";");
    }
    async Task StartDemo(bool auto)
    {
        // Self-tests execute only against the local fixture. No live-site automation is performed here.
        if (!view.CoreWebView2.Source.StartsWith(LocalBase + "demo.html")) throw new InvalidOperationException("로컬 모의 화면에서만 테스트할 수 있습니다.");
        await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
          const s=document.getElementById('korail-one-helper').shadowRoot;
          s.getElementById('limit').value='1';
          s.getElementById('pickSearch').click();document.getElementById('query').click();
          s.getElementById('pickSeat').click();document.querySelector('#trains tr:first-child td:nth-child(4)').click();
          s.getElementById('auto').checked=" + (auto ? "true" : "false") + @";
          s.getElementById('confirmed').checked=true;s.getElementById('start').click();
        })();");
    }
    async Task LoadCardDemo(string scenario)
    {
        loadWaiter = new TaskCompletionSource<bool>();
        Navigate(LocalBase + "card-demo.html?case=" + Uri.EscapeDataString(scenario) + "&test=" + Guid.NewGuid().ToString("N"));
        var done = await Task.WhenAny(loadWaiter.Task, Task.Delay(15000));
        if (done != loadWaiter.Task) throw new TimeoutException("카드 모의 화면 로딩 제한시간 초과");
        await loadWaiter.Task; loadWaiter = null;
    }
    async Task SelectCardTrain(string train, string grade)
    {
        if (!Program.SelfTest || !view.CoreWebView2.Source.StartsWith(LocalBase + "card-demo.html")) throw new InvalidOperationException("로컬 카드 모의 화면 전용 검사입니다.");
        await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
          const s=document.getElementById('korail-one-helper').shadowRoot;
          s.getElementById('grade').value=" + json.Serialize(grade) + @";
          s.getElementById('grade').dispatchEvent(new Event('change'));s.getElementById('scanTrains').click();
        })();");
        Assert(await EvalBool("window.KorailDesktopHelper.snapshot().candidates.some(c=>c.train===" + json.Serialize(train) + ") && !window.KorailDesktopHelper.snapshot().target"), "card picker: scanned train requires explicit selection / " + train + " / " + grade + " / " + await EvalString("window.KorailDesktopHelper.snapshot().status"));
        await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
          const s=document.getElementById('korail-one-helper').shadowRoot;
          const index=window.KorailDesktopHelper.snapshot().candidates.findIndex(c=>c.train===" + json.Serialize(train) + @");
          s.getElementById('trainChoice').value=String(index);s.getElementById('trainChoice').dispatchEvent(new Event('change'));s.getElementById('applyTrain').click();
          s.getElementById('limit').value='1';s.getElementById('confirmed').checked=true;
        })();");
        Assert(await EvalBool("window.KorailDesktopHelper.snapshot().target?.train===" + json.Serialize(train) + " && !!window.KorailDesktopHelper.snapshot().search"), "card picker: chosen identity and active filter refresh retained / " + train);
    }
    async Task StartCardDemo(bool auto)
    {
        if (!Program.SelfTest || !view.CoreWebView2.Source.StartsWith(LocalBase + "card-demo.html")) throw new InvalidOperationException("로컬 카드 모의 화면 전용 검사입니다.");
        await view.CoreWebView2.ExecuteScriptAsync("(() => {const s=document.getElementById('korail-one-helper').shadowRoot;s.getElementById('auto').checked=" + (auto ? "true" : "false") + ";s.getElementById('confirmed').checked=true;s.getElementById('start').click();})();");
        await Task.Delay(300); await WaitStopped(12000); await Task.Delay(250);
    }
    async Task RunCardTests()
    {
        await LoadCardDemo("available");
        Assert(await EvalBool("KorailSiteAdapter.matchesPage(document) && KorailSiteAdapter.query(document).supported && KorailSiteAdapter.cards(document).length===6"), "live card adapter: recognizes six direct cards in sanitized local fixture");
        Assert(await EvalBool("(() => {const S=KorailSiteAdapter;return !!S.positiveAction(S.slot(__cardDemo.row('505'),'일반실'),'일반실') && !S.positiveAction(S.slot(__cardDemo.row('505'),'특실'),'특실') && !S.positiveAction(S.slot(__cardDemo.row('521'),'일반실'),'일반실') && !!S.positiveAction(S.slot(__cardDemo.row('513'),'특실'),'특실');})()"), "live card adapter: available and imminent seats accepted, sold-out and standing-plus-seat rejected");
        Assert(await EvalBool("(() => {const S=KorailSiteAdapter;const q=()=>document.cloneNode(true);for(const alter of [d=>d.getElementById('rtYn').checked=true,d=>d.querySelector('.select.select02>.selected').textContent='환승',d=>d.getElementById('labelple').value='총 2명']){const d=q();alter(d);if(S.query(d).supported)return false;}const d=q(),before=JSON.stringify(S.query(d));d.querySelector('.txt_price').textContent='99,000원';d.querySelector('.tckList').remove();const button=d.querySelector('#btn_tab_info1>button');button.disabled=true;return JSON.stringify(S.query(d))===before && S.activeRefresh(d)?.selector==='#btn_tab_info1 > button';})()"), "live card query: unsupported routes/passengers rejected; fare/list/loading changes preserve query and filter identity");
        Assert(await EvalBool("(() => {const S=KorailSiteAdapter,source=__cardDemo.row('513');let row=source.cloneNode(true);row.querySelector('.tck_inner').append(row.querySelector('.price_box').cloneNode(true));if(S.slot(row,'일반실'))return false;row=source.cloneNode(true);row.querySelector('.price_box.gen .txt_ch').textContent='특실';if(S.slot(row,'일반실'))return false;row=source.cloneNode(true);row.querySelector('.data_box h3 span:last-child').textContent='(25:61 ~ 14:01)';if(S.identity(row))return false;row=source.cloneNode(true);row.querySelector('.flag_wrap .blind').textContent='UNKNOWN';return !S.identity(row);})()"), "live card parser: ambiguous grade structure, invalid clocks and unknown train kind rejected");
        int before = demoNotifications.Count;
        await SelectCardTrain("KTX 505", "일반실"); await StartCardDemo(false);
        Assert(await EvalBool("__cardDemo.snapshot().gradeClicks===0 && __cardDemo.snapshot().reserveClicks===0 && document.title.includes('좌석 발견')") && demoNotifications.Count == before, "card alert-only: finds selected seat without grade selection, reservation or Telegram notice");
        foreach (string grade in new [] { "일반실", "특실" })
        {
            await LoadCardDemo("available"); before = demoNotifications.Count;
            await SelectCardTrain("KTX 513", grade); await StartCardDemo(true);
            Assert(await EvalBool("__cardDemo.snapshot().gradeClicks===1 && __cardDemo.snapshot().reserveClicks===1 && __cardDemo.snapshot().lastTrain==='513' && __cardDemo.snapshot().lastGrade===" + json.Serialize(grade)), "card auto: grade selection then actual drawer reservation exactly once / " + grade + " / " + await EvalString("window.KorailDesktopHelper.snapshot().status"));
            Assert(demoNotifications.Count == before + 1 && demoNotifications.Last().Train == "KTX 513" && demoNotifications.Last().Grade == grade && !demoNotifications.Last().Manual, "card auto: only drawer reservation produces one native notice / " + grade);
        }
        foreach (string scenario in new [] { "refresh", "reorder" })
        {
            await LoadCardDemo(scenario); before = demoNotifications.Count;
            await SelectCardTrain("KTX-산천 541", "일반실"); await StartCardDemo(true);
            Assert(await EvalBool("(() => {const s=__cardDemo.snapshot();return s.filterQueries===1 && s.mainQueries===0 && s.restoredAt-s.clearedAt>=1300 && s.lastTrain==='541' && s.reserveClicks===1;})()"), "card refresh: active filter survives removed list and retains train identity / " + scenario + " / " + await EvalString("window.KorailDesktopHelper.snapshot().status"));
            Assert(demoNotifications.Count == before + 1 && demoNotifications.Last().Train == "KTX-산천 541", "card refresh: one notice after confirmed new result / " + scenario);
        }
        await LoadCardDemo("mismatch"); before = demoNotifications.Count;
        await SelectCardTrain("KTX 513", "일반실"); await StartCardDemo(true);
        Assert(await EvalBool("__cardDemo.snapshot().gradeClicks===1 && __cardDemo.snapshot().reserveClicks===0") && demoNotifications.Count == before, "card auto: wrong drawer grade prevents reservation and notification");

        await LoadCardDemo("available"); before = demoNotifications.Count;
        await SelectCardTrain("KTX 513", "일반실");
        await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('korail-one-helper').shadowRoot.getElementById('fold').click();");
        await ClickDemoButton("[data-fixture-train='513'] .price_box.spe a");
        Assert(demoNotifications.Count == before && await EvalBool("__cardDemo.snapshot().gradeClicks===1 && __cardDemo.snapshot().reserveClicks===0"), "card manual: clicking a grade selector never notifies");
        await ClickDemoButton(".ticket_reserv_wrap .reservbtn");
        Assert(demoNotifications.Count == before && await EvalBool("__cardDemo.snapshot().reserveClicks===1"), "card manual: unselected grade reservation sends no notice");
        await ClickDemoButton("[data-fixture-train='513'] .price_box.gen a");
        Assert(demoNotifications.Count == before, "card manual: matching grade selection still sends no notice");
        Assert(await EvalBool("(() => {const S=KorailSiteAdapter,row=__cardDemo.row('513'),drawer=document.querySelector('.ticket_reserv_wrap');if(!S.reservationAction(document,row,'일반실'))return false;drawer.hidden=true;const hiddenRejected=!S.reservationAction(document,row,'일반실');drawer.hidden=false;const other=S.slot(__cardDemo.row('505'),'일반실');other.classList.add('active');const duplicateRejected=!S.reservationAction(document,row,'일반실');other.classList.remove('active');return hiddenRejected&&duplicateRejected;})()"), "card drawer: hidden or multiple active selections rejected");
        await ClickDemoButton(".ticket_reserv_wrap .reservbtn");
        Assert(demoNotifications.Count == before + 1 && demoNotifications.Last().Manual && demoNotifications.Last().Train == "KTX 513" && demoNotifications.Last().Grade == "일반실", "card manual: actual matching drawer reservation sends one native notice");
        using (var image = File.Create(Path.Combine(Path.GetDirectoryName(Program.TestOutput), "card-demo.png")))
            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
    }
    async Task WaitStopped(int timeout)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < end)
        {
            if (await EvalBool("window.KorailDesktopHelper && !window.KorailDesktopHelper.snapshot().running && !window.KorailDesktopHelper.snapshot().starting")) return;
            await Task.Delay(150);
        }
        throw new TimeoutException("자동 중지 실패");
    }
    async Task ClickDemoButton(string selector)
    {
        if (!Program.SelfTest || !(view.CoreWebView2.Source.StartsWith(LocalBase + "demo.html") || view.CoreWebView2.Source.StartsWith(LocalBase + "card-demo.html"))) throw new InvalidOperationException("모의 검사 전용 입력입니다.");
        string point = await view.CoreWebView2.ExecuteScriptAsync("(() => { const b=document.querySelector(" + json.Serialize(selector) + ");b.scrollIntoView({block:'center'});const r=b.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()");
        var position = json.Deserialize<Dictionary<string, object>>(point);
        // Chromium input creates trusted events only in our local, non-network fixture.
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mousePressed", x = position["x"], y = position["y"], button = "left", clickCount = 1 }));
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json.Serialize(new { type = "mouseReleased", x = position["x"], y = position["y"], button = "left", clickCount = 1 }));
        await Task.Delay(250);
    }
    void RenderSettingsPreview()
    {
        var preview = new AppSettings { Journey = new JourneySettings { Enabled = true, DepartureStation = "서울", ArrivalStation = "부산", Date = "2026-10-25", FromTime = "07:00", ToTime = "10:00", TrainNumber = "103", Grade = "일반실" } };
        foreach (bool telegram in new [] { false, true })
        using (var dialog = new SettingsDialog(preview, telegram))
        {
            dialog.Opacity = 0; dialog.Show(this); dialog.PerformLayout(); dialog.Refresh();
            SaveDialogPreview(dialog, telegram ? "telegram-settings-preview.png" : "settings-preview.png");
            dialog.Size = dialog.MinimumSize; dialog.PerformLayout(); dialog.Refresh();
            SaveDialogPreview(dialog, telegram ? "telegram-settings-compact-preview.png" : "settings-compact-preview.png");
            dialog.Close();
        }
        using (var firstRun = new SettingsDialog(new AppSettings(), true, true))
        {
            firstRun.Opacity = 0; firstRun.Show(this); firstRun.PerformLayout(); firstRun.Refresh();
            SaveDialogPreview(firstRun, "first-run-preview.png");
            firstRun.Close();
        }
    }
    void SaveDialogPreview(Form dialog, string name)
    {
        using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
        {
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(Program.TestOutput), name), System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    void Assert(bool pass, string name) { if (!pass) throw new Exception("FAIL: " + name); testResults.Add("PASS: " + name); }
    async Task RunRefreshTimingTests()
    {
        if (!Program.SelfTest) throw new InvalidOperationException("로컬 모의 검사 전용입니다.");
        await LoadDemo("available");
        int savedInterval = mouseRefreshIntervalSeconds, savedJitter = mouseRefreshJitterPercent;
        DateTime savedNext = mouseRefreshNext;
        try
        {
            DateTime now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
            mouseRefreshIntervalSeconds = 60; mouseRefreshJitterPercent = 0;
            ScheduleNextMouseRefresh(now);
            Assert(mouseRefreshNext == now.AddSeconds(60), "refresh: zero jitter keeps the fixed deadline");
            mouseRefreshJitterPercent = 20;
            bool bounded = true;
            for (int i = 0; i < 10; i++)
            {
                ScheduleNextMouseRefresh(now);
                bounded &= mouseRefreshNext >= now.AddSeconds(48) && mouseRefreshNext <= now.AddSeconds(72);
            }
            Assert(bounded, "refresh: host schedules sampled deadlines in the configured range");
            DateTime chosen = mouseRefreshNext;
            await Task.Delay(30);
            Assert(mouseRefreshNext == chosen, "refresh: elapsed waiting does not replace the chosen deadline");

            Exception dialogError = null; bool inspected = false;
            using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
            {
                timer.Tick += delegate {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.Text == "마우스 자동 조회");
                    if (dialog == null) return;
                    timer.Stop();
                    try
                    {
                        dialog.Opacity = 0;
                        var interval = dialog.Controls.OfType<TextBox>().Single(control => control.AccessibleName == "조회 간격");
                        var count = dialog.Controls.OfType<TextBox>().Single(control => control.AccessibleName == "추가 조회 횟수");
                        var jitter = dialog.Controls.OfType<TextBox>().Single(control => control.AccessibleName == "랜덤 지연 비율");
                        var range = dialog.Controls.OfType<Label>().Single(control => control.AccessibleName == "실제 대기 범위");
                        var error = dialog.Controls.OfType<Label>().Single(control => control.AccessibleName == "입력 오류");
                        var start = dialog.Controls.OfType<Button>().Single(control => control.AccessibleName == "자동 조회 시작");
                        interval.Text = "60"; jitter.Text = "20";
                        Assert(range.Text == "실제 대기: 48~72초", "refresh dialog: interval and percent update the displayed range");
                        interval.Text = "5"; count.Text = "9999999"; jitter.Text = "0";
                        count.Focus(); dialog.Validate();
                        Assert(interval.Text == "5" && count.Text == "9999999" && start.Enabled && range.Text == "실제 대기: 5~5초", "refresh dialog: typed five seconds and 9999999 queries survive validation without clamping");
                        jitter.Text = "20";
                        Assert(range.Text == "실제 대기: 4~6초", "refresh dialog: five-second jitter uses the requested base");
                        interval.Text = "300";
                        Assert(range.Text == "실제 대기: 240~360초", "refresh dialog: jitter is no longer clipped at three hundred seconds");
                        interval.Text = "0";
                        Assert(!start.Enabled && interval.Text == "0" && !String.IsNullOrEmpty(error.Text), "refresh dialog: invalid interval stays visible and prevents start");
                        interval.Text = "5"; count.Text = "2147483648";
                        Assert(!start.Enabled && count.Text == "2147483648" && !String.IsNullOrEmpty(error.Text), "refresh dialog: overflowing count is rejected instead of silently capped");
                        count.Text = "9999999"; jitter.Text = "101";
                        Assert(!start.Enabled && jitter.Text == "101", "refresh dialog: invalid percentage is not silently capped");
                        jitter.Text = "0";
                        using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
                        {
                            dialog.PerformLayout(); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(Program.TestOutput), "refresh-settings-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        interval.Text = Int32.MaxValue.ToString(); jitter.Text = "1";
                        SaveDialogPreview(dialog, "refresh-settings-max-preview.png");
                        jitter.Text = "55"; inspected = true;
                    }
                    catch (Exception ex) { dialogError = ex; }
                    finally { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
                };
                timer.Start();
                await OpenMouseRefresh();
            }
            if (dialogError != null) throw dialogError;
            Assert(inspected && mouseRefreshJitterPercent == 20 && !mouseBookingActive, "refresh dialog: cancel neither changes saved options nor starts input");
            using (var accepted = new RefreshSettingsDialog(new RefreshOptions(60, 10, 0), Font))
            {
                accepted.ShowInTaskbar = false; accepted.Opacity = 0; accepted.Show(this);
                accepted.Controls.OfType<TextBox>().Single(control => control.AccessibleName == "조회 간격").Text = "5";
                accepted.Controls.OfType<TextBox>().Single(control => control.AccessibleName == "추가 조회 횟수").Text = "999999";
                accepted.Controls.OfType<Button>().Single(control => control.AccessibleName == "자동 조회 시작").PerformClick();
                Assert(accepted.DialogResult == DialogResult.OK && accepted.SelectedOptions.IntervalSeconds == 5 && accepted.SelectedOptions.Limit == 999999,
                    "refresh dialog: accepting typed values returns the exact five-second and 999999-query settings without native input");
                accepted.Close();
            }
        }
        finally { mouseRefreshIntervalSeconds = savedInterval; mouseRefreshJitterPercent = savedJitter; mouseRefreshNext = savedNext; }
    }
    async Task RunTests()
    {
        try
        {
            await RunRefreshTimingTests();
            RenderSettingsPreview();
            await RunShellPreviewChecks();
            await RunBookingQueueSelfTests();
            var testLease = new LeaseGate();
            Assert(testLease.Request("acquire", "old", true) && !testLease.Request("acquire", "new", true), "native lease: one owner");
            testLease.Request("release", "other", true);
            Assert(testLease.Request("check", "old", true), "native lease: wrong owner release ignored");
            testLease.Reset(); testLease.Request("acquire", "new", true); testLease.Request("release", "old", true);
            Assert(testLease.Request("check", "new", true) && !testLease.Request("check", "new", false), "native lease: old run and inactive window rejected");
            foreach (string scenario in new [] { "available", "reorder", "waiting", "disabled", "error", "condition", "unstable" })
            {
                int notificationsBefore = demoNotifications.Count;
                await LoadDemo(scenario); await StartDemo(true); await Task.Delay(300); await WaitStopped(10000);
                await Task.Delay(200);
                string result = await EvalString("document.getElementById('result').textContent");
                string helperStatus = await EvalString("document.getElementById('korail-one-helper').shadowRoot.getElementById('status').textContent");
                bool positive = scenario == "available" || scenario == "reorder";
                Assert(positive ? result.Contains("1회") && result.Contains("KTX 101") : result.Contains("0회"), scenario + " / " + result + " / " + helperStatus);
                if (positive) Assert(helperStatus.Contains("좌석 발견"), "positive must detect seat through native bridge");
                Assert(demoNotifications.Count - notificationsBefore == (positive ? 1 : 0), scenario + ": one click notification only for actual auto click");
                if (scenario == "reorder") using (var image = File.Create(Path.Combine(Path.GetDirectoryName(Program.TestOutput), "desktop-demo.png")))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            foreach (string grade in new [] { "일반실", "특실" })
            {
                await LoadDemo("available");
                await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
                  const s=document.getElementById('korail-one-helper').shadowRoot;
                  s.getElementById('grade').value=" + json.Serialize(grade) + @";
                  s.getElementById('grade').dispatchEvent(new Event('change'));
                  s.getElementById('scanTrains').click();
                })();");
                Assert(await EvalBool("window.KorailDesktopHelper.snapshot().candidates.length===2 && !window.KorailDesktopHelper.snapshot().target"), "train picker: scan alone does not choose a train / " + grade);
                int before = demoNotifications.Count;
                await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
                  const s=document.getElementById('korail-one-helper').shadowRoot;
                  s.getElementById('trainChoice').value='1';s.getElementById('trainChoice').dispatchEvent(new Event('change'));
                  s.getElementById('applyTrain').click();
                  s.getElementById('pickSearch').click();document.getElementById('query').click();
                  s.getElementById('auto').checked=true;s.getElementById('confirmed').checked=true;s.getElementById('start').click();
                })();");
                await Task.Delay(300); await WaitStopped(10000); await Task.Delay(200);
                Assert(await EvalBool("document.getElementById('result').dataset.train==='103' && document.getElementById('result').dataset.grade===" + json.Serialize(grade)), "train picker: user-selected KTX 103 / " + grade);
                Assert(demoNotifications.Count == before + 1 && demoNotifications.Last().Train == "KTX 103" && demoNotifications.Last().Grade == grade, "native click notification carries selected train and grade / " + grade);
            }
            await RunCardTests();
            // A click may navigate before its queued WebMessageReceived event arrives.
            await LoadDemo("available");
            int beforeNavigationClick = demoNotifications.Count;
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              const s=document.getElementById('korail-one-helper').shadowRoot;
              s.getElementById('scanTrains').click();s.getElementById('trainChoice').value='1';
              s.getElementById('trainChoice').dispatchEvent(new Event('change'));s.getElementById('applyTrain').click();
              s.getElementById('pickSearch').click();document.getElementById('query').click();
              document.querySelector('[data-book=""103""]').addEventListener('click',()=>{location.href='welcome.html';});
              s.getElementById('auto').checked=true;s.getElementById('confirmed').checked=true;s.getElementById('start').click();
            })();");
            await Task.Delay(1700);
            Assert(view.CoreWebView2.Source.Contains("welcome.html") && demoNotifications.Count == beforeNavigationClick + 1, "immediate navigation after click preserves exactly one native notification");
            await LoadDemo("available");
            int beforeManual = demoNotifications.Count;
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              const s=document.getElementById('korail-one-helper').shadowRoot;
              s.getElementById('scanTrains').click();s.getElementById('trainChoice').value='1';
              s.getElementById('trainChoice').dispatchEvent(new Event('change'));s.getElementById('applyTrain').click();
              s.getElementById('fold').click();
            })();");
            await ClickDemoButton("[data-book='103'][data-grade='특실']");
            Assert(demoNotifications.Count == beforeManual, "manual click on unselected seat grade sends no notification");
            await ClickDemoButton("[data-book='103']:not([data-grade])");
            Assert(demoNotifications.Count == beforeManual + 1 && demoNotifications.Last().Manual && demoNotifications.Last().Train == "KTX 103", "trusted manual click on selected seat sends one native notification");
            await LoadDemo("available");
            await view.CoreWebView2.ExecuteScriptAsync(@"(() => {
              const s=document.getElementById('korail-one-helper').shadowRoot;
              s.getElementById('pickSearch').click();document.getElementById('query').click();
              s.getElementById('scanTrains').click();s.getElementById('trainChoice').value='1';
              s.getElementById('trainChoice').dispatchEvent(new Event('change'));s.getElementById('applyTrain').click();
              s.getElementById('confirmed').checked=true;
            })();");
            Assert(await EvalBool("!!window.KorailDesktopHelper.snapshot().target && !!window.KorailDesktopHelper.snapshot().search"), "SPA reset: fixture starts with chosen train and search button");
            await view.CoreWebView2.ExecuteScriptAsync("history.pushState({},'', '/practice-results');");
            await Task.Delay(500);
            Assert(!activeDocument && await EvalBool("(() => {const s=window.KorailDesktopHelper.snapshot();return !s.target && !s.search && !s.confirmed && !s.running && !s.starting && s.candidates.length===0;})()"), "SPA route change clears search/train/confirmation and stops monitoring");
            await view.CoreWebView2.ExecuteScriptAsync("history.replaceState({},'', '/demo.html');");
            await Task.Delay(500);
            Assert(activeDocument && await EvalBool("!window.KorailDesktopHelper.snapshot().target && !window.KorailDesktopHelper.snapshot().running"), "SPA return requires explicit reselection without auto-resuming");
            int beforeAlert = demoNotifications.Count;
            await LoadDemo("available"); await StartDemo(false); await Task.Delay(300); await WaitStopped(10000);
            Assert((await EvalString("document.getElementById('result').textContent")).Contains("0회") && (await EvalString("document.title")).Contains("좌석 발견"), "alert-only: no click");
            Assert(demoNotifications.Count == beforeAlert, "alert-only: no reservation click notification");
            await LoadDemo("available"); await StartDemo(true); await StopHelper("수동 중지 시험"); await Task.Delay(2200);
            Assert((await EvalString("document.getElementById('result').textContent")).Contains("0회"), "manual native stop: no pending click");
            await LoadDemo("available"); await StartDemo(true); WindowState = FormWindowState.Minimized; await Task.Delay(2200); WindowState = FormWindowState.Normal;
            // WebView's visibility notification can arrive after the native
            // window restore. Wait for that event before testing a new start.
            DateTime visibleDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < visibleDeadline && !await EvalBool("!document.hidden")) await Task.Delay(50);
            Assert(await EvalBool("!document.hidden"), "restore: WebView becomes visible before an explicit restart");
            Assert((await EvalString("document.getElementById('result').textContent")).Contains("0회"), "minimize: no pending click");
            Assert(activeDocument && !(await EvalBool("window.KorailDesktopHelper.snapshot().running")), "restore: allows explicit restart without auto-resuming");
            await StartDemo(true); await Task.Delay(300); await WaitStopped(10000);
            Assert((await EvalString("document.getElementById('result').textContent")).Contains("1회"), "restore: explicit restart works / " + await EvalString("window.KorailDesktopHelper.snapshot().status"));
            await LoadDemo("available"); await StartDemo(true); Navigate(LocalBase + "welcome.html"); await Task.Delay(1800);
            Assert(view.CoreWebView2.Source.Contains("welcome.html") && !activeDocument, "navigation clears active document");
            await LoadDemo("unchanged"); await StartDemo(true); await Task.Delay(300); await WaitStopped(35000);
            Assert((await EvalString("document.getElementById('result').textContent")).Contains("0회") && (await EvalString("document.getElementById('korail-one-helper').shadowRoot.getElementById('status').textContent")).Contains("갱신"), "unconfirmed refresh: timeout without click");
            await RunMouseMoreTests();
            await RunRefreshLimitTests();
            await RunMousePauseTests();
            FinishTests(null);
        }
        catch (Exception ex) { FinishTests(ex); }
    }
    void FinishTests(Exception error)
    {
        if (error != null) testResults.Add(error.ToString());
        if (Program.TestOutput != null) File.WriteAllText(Program.TestOutput, json.Serialize(new { success = error == null, tests = testResults.ToArray(), runtime = ready ? view.CoreWebView2.Environment.BrowserVersionString : "unavailable" }));
        Environment.ExitCode = error == null ? 0 : 1;
        Close();
    }
}
