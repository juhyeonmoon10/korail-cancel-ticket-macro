using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

internal sealed partial class MainWindow
{
    readonly ToolTip shellTips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 450, ReshowDelay = 100 };
    readonly Label shellState = new Label { AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(10, 0, 0, 0) };

    void InitializeDesktopShell()
    {
        // Establish the launch mode before wiring CheckedChanged so constructing
        // the shell never invokes the transport-change stop handler.
        backgroundInput = !Program.ManualDiagnostic;
        UiTheme.StyleForm(this);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 204));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = Padding.Empty, Padding = new Padding(16, 20, 16, 16) };
        var brand = new Panel { Dock = DockStyle.Top, Height = 100 };
        brand.Controls.Add(new Label { Text = "한 장 도우미", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = UiTheme.Text, AutoSize = true, Location = new Point(0, 4) });
        brand.Controls.Add(new Label { Text = "KORAIL HELPER  /  0.12.0", Font = new Font(Font.FontFamily, 8), ForeColor = UiTheme.Muted, AutoSize = true, Location = new Point(2, 48) });
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = Padding.Empty };
        AddNavButton(nav, "코레일 열기", delegate { Navigate("https://www.korail.com/ticket/main"); });
        AddNavButton(nav, "예약 관리", async delegate { await OpenBookingQueue(); }).Enabled = !Program.ManualDiagnostic;
        AddNavButton(nav, "설정", async delegate { await EditSettings(false); }).Enabled = !Program.ManualDiagnostic;
        var tools = AddNavButton(nav, "도움말  ›", delegate { });
        var menu = new ContextMenuStrip { Font = Font, ShowImageMargin = false };
        menu.Items.Add("사용 안내", null, delegate { Navigate(LocalBase + "welcome.html"); });
        menu.Items.Add("마우스 연습", null, delegate { Navigate(LocalBase + "card-demo.html?case=mouse-notice"); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("화면 진단 저장", null, async delegate { await SaveDiagnostics(); });
        menu.Items.Add("브라우저 진단 저장", null, delegate { SaveBrowserDiagnostics(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("이전 연습 화면", null, delegate { Navigate(LocalBase + "demo.html"); });
        tools.Click += delegate { menu.Show(tools, new Point(tools.Width, 0)); };
        var sidebarNote = new Label { Dock = DockStyle.Bottom, Height = 100, ForeColor = UiTheme.Muted, Font = new Font(Font.FontFamily, 9), Text = "F8  언제든 중지\n\n동시 작업 모드에서도\n최소화하지 마세요." };
        sidebar.Controls.Add(nav); sidebar.Controls.Add(brand); sidebar.Controls.Add(sidebarNote);

        var workspace = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 16, 18, 14), BackColor = UiTheme.Background };
        var commands = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2, AutoSize = true, Padding = new Padding(0, 0, 0, 12), Margin = Padding.Empty };
        commands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commands.RowStyles.Add(new RowStyle(SizeType.AutoSize)); commands.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        commands.Controls.Add(new Label { Text = "한 편 또는 모두 예약 · 다음 일정은 예약 관리", AutoSize = true, Font = new Font(Font.FontFamily, 14, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) }, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
        refreshButton = AddActionButton(actions, "선택 열차 시작", async delegate { await OpenMouseRefresh(); }, "primary");
        bookingButton = refreshButton;
        mouseContinueButton = AddActionButton(actions, "계속", async delegate { await ResumeMouseBooking(); });
        mouseContinueButton.Enabled = false;
        AddActionButton(actions, "모두 중지 (F8)", async delegate { await StopHelper("사용자가 모두 중지했습니다."); }, "danger");
        backgroundInputCheck = new CheckBox { Text = "백그라운드 실행 (기본)", Checked = backgroundInput, AutoSize = true, Margin = new Padding(8, 11, 8, 4), AccessibleName = "다른 앱과 동시 작업", Enabled = !Program.ManualDiagnostic };
        shellTips.SetToolTip(backgroundInputCheck, "기본으로 켜집니다. 다른 앱에서 마우스·키보드를 사용해도 도우미 웹 화면에만 입력하며 계속 실행합니다. 해제하면 실제 커서를 사용하는 마우스 모드로 전환합니다. 최소화·화면 잠금은 중지하며 실제 코레일 호환성은 미검증입니다. 변경하면 현재 실행이 중지됩니다.");
        backgroundInputCheck.CheckedChanged += ChangeBackgroundInput;
        actions.Controls.Add(backgroundInputCheck);
        pageNoticeButton = AddActionButton(actions, "안내 확인", delegate { OpenDeferredPageNotice(); });
        pageNoticeButton.Visible = false;
        bookingButton.Enabled = refreshButton.Enabled = !Program.ManualDiagnostic;
        siteDiagnostics.ManualMode = Program.ManualDiagnostic;
        commands.Controls.Add(actions, 0, 1);

        var browser = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Padding = new Padding(1) };
        var locationBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 2, Padding = new Padding(12, 7, 6, 5), BackColor = UiTheme.Surface };
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        address.Dock = DockStyle.Fill; address.ReadOnly = true; address.BorderStyle = BorderStyle.None;
        address.BackColor = UiTheme.Surface; address.ForeColor = UiTheme.Muted; address.TabStop = false; address.AccessibleName = "현재 페이지 주소";
        var reload = new Button { Text = "새로고침", Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        UiTheme.StyleButton(reload, "quiet"); reload.Padding = Padding.Empty;
        reload.Click += async delegate { await StopHelper("새로고침으로 중지했습니다."); if (ready) view.Reload(); };
        locationBar.Controls.Add(address, 0, 0); locationBar.Controls.Add(reload, 1, 0);
        view.Dock = DockStyle.Fill; view.DefaultBackgroundColor = Color.White;
        browser.Controls.Add(view); browser.Controls.Add(locationBar);

        var feedback = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 184, ColumnCount = 1, RowCount = 5, BackColor = UiTheme.Surface, Padding = new Padding(14, 10, 14, 8), Margin = Padding.Empty };
        feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); feedback.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var stateHeading = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        stateHeading.Controls.Add(new Label { Text = "실행 상태", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 3, 0, 0) });
        stateHeading.Controls.Add(shellState);
        status.Dock = DockStyle.Fill; status.Margin = new Padding(0, 4, 0, 4); status.AutoEllipsis = true; status.AccessibleName = "실행 상태 안내";
        status.Text = Program.ManualDiagnostic ? "준비 중 · 수동 진단 모드 · 자동 기능 꺼짐"
            : "준비 중 · 다른 앱에서 마우스·키보드를 사용해도 백그라운드 실행은 계속됩니다.";
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        notificationStatus.Dock = sleepStatus.Dock = DockStyle.Fill;
        notificationStatus.Font = sleepStatus.Font = new Font(Font.FontFamily, 9);
        notificationStatus.ForeColor = sleepStatus.ForeColor = UiTheme.Muted;
        notificationStatus.Margin = new Padding(0, 2, 12, 0); sleepStatus.Margin = new Padding(6, 2, 0, 0);
        notificationStatus.AutoEllipsis = sleepStatus.AutoEllipsis = true;
        notificationStatus.Text = settings.Telegram.Enabled ? "텔레그램 알림 켜짐 · 예약 완료 및 설정한 실행 보고" : "텔레그램 알림 꺼짐 · ‘설정 → 내 텔레그램 연결’에서 연결하세요.";
        if (settings.LoadWarning != null) notificationStatus.Text = settings.LoadWarning;
        if (Program.ManualDiagnostic) notificationStatus.Text = "수동 진단 모드 · 자동 예약·알림 없음 · 기존 브라우저 세션 유지";
        ShowSleepStatus(false);
        details.Controls.Add(notificationStatus, 0, 0); details.Controls.Add(sleepStatus, 1, 0);
        refreshProgress.Dock = DockStyle.Fill; refreshProgress.AutoEllipsis = true;
        refreshProgress.ForeColor = UiTheme.Accent; refreshProgress.Font = new Font(Font.FontFamily, 9);
        refreshProgress.Margin = new Padding(0, 0, 0, 4); UpdateRefreshProgress();
        feedback.Controls.Add(stateHeading, 0, 0); feedback.Controls.Add(status, 0, 1); feedback.Controls.Add(refreshProgress, 0, 2); feedback.Controls.Add(details, 0, 3);
        automationReportStatus.Dock = DockStyle.Fill; automationReportStatus.AutoEllipsis = true;
        automationReportStatus.Font = new Font(Font.FontFamily, 9); automationReportStatus.ForeColor = UiTheme.Muted;
        automationReportStatus.Text = "실행 보고 · 시작·오류·중간 보고는 내 텔레그램 연결에서 설정하세요.";
        feedback.Controls.Add(automationReportStatus, 0, 4);
        foreach (var label in new [] { status, notificationStatus, sleepStatus, automationReportStatus })
        {
            var observed = label;
            shellTips.SetToolTip(observed, observed.Text);
            observed.TextChanged += delegate { shellTips.SetToolTip(observed, observed.Text); UpdateShellState(); };
        }
        mouseContinueButton.EnabledChanged += delegate { UpdateShellState(); };
        workspace.Controls.Add(browser); workspace.Controls.Add(commands); workspace.Controls.Add(feedback);
        shell.Controls.Add(sidebar, 0, 0); shell.Controls.Add(workspace, 1, 0); Controls.Add(shell);
        UpdateShellState();
        FormClosed += delegate { shellTips.Dispose(); menu.Dispose(); };
    }

    static void AddNavHeading(FlowLayoutPanel nav, string text)
    {
        nav.Controls.Add(new Label { Text = text, Width = 166, Height = 26, ForeColor = UiTheme.Muted, Font = new Font("맑은 고딕", 9), Margin = new Padding(6, 14, 0, 4) });
    }

    Button AddNavButton(FlowLayoutPanel nav, string title, EventHandler handler)
    {
        var button = new Button { Text = title, Width = 168, Height = 43, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 5), AccessibleName = title };
        UiTheme.StyleButton(button, "quiet"); button.Click += handler; nav.Controls.Add(button); return button;
    }

    Button AddActionButton(FlowLayoutPanel panel, string title, EventHandler handler, string variant = "secondary")
    {
        var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(66, 42), Margin = new Padding(0, 0, 8, 4), AccessibleName = title };
        UiTheme.StyleButton(button, variant); button.Click += handler; panel.Controls.Add(button); return button;
    }

    void UpdateShellState()
    {
        bool blocked = siteDiagnostics.SiteBlocked || mouseRefreshSiteBlocked;
        shellState.Text = Program.ManualDiagnostic ? "수동 진단" : blocked ? "중지됨" : mousePaused ? "일시정지" : queueRunning ? "예약 순서 실행 중" : mouseBookingActive ? "실행 중" : "대기";
        if (BackgroundInputEnabled) shellState.Text += " · 동시 작업";
        shellState.BackColor = blocked ? Color.FromArgb(255, 233, 235) : mousePaused ? Color.FromArgb(255, 243, 218) : mouseBookingActive ? Color.FromArgb(228, 242, 235) : Color.FromArgb(234, 239, 248);
        shellState.ForeColor = blocked ? UiTheme.Danger : mousePaused ? Color.FromArgb(139, 89, 13) : mouseBookingActive ? Color.FromArgb(30, 111, 78) : UiTheme.Muted;
        UpdateRefreshProgress();
    }

    async Task RenderShellPreview(string name)
    {
        if (!Program.SelfTest || !view.CoreWebView2.Source.StartsWith(LocalBase, StringComparison.Ordinal)) return;
        using (var page = new MemoryStream())
        {
            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, page);
            using (var bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                page.Position = 0;
                using (var content = Image.FromStream(page))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var clientOrigin = new Point((Width - ClientSize.Width) / 2, Height - ClientSize.Height - (Width - ClientSize.Width) / 2);
                    Point browserOrigin = PointToClient(view.PointToScreen(Point.Empty)); browserOrigin.Offset(clientOrigin);
                    graphics.DrawImage(content, new Rectangle(browserOrigin, view.ClientSize));
                }
                bitmap.Save(Path.Combine(Path.GetDirectoryName(Program.TestOutput), name), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }

    async Task RunShellPreviewChecks()
    {
        if (!Program.SelfTest) return;
        loadWaiter = new TaskCompletionSource<bool>();
        Navigate(LocalBase + "welcome.html");
        var loaded = await Task.WhenAny(loadWaiter.Task, Task.Delay(15000));
        if (loaded != loadWaiter.Task) throw new TimeoutException("안내 화면 로딩 제한시간 초과");
        await loadWaiter.Task; loadWaiter = null;
        Size original = Size;
        bool noticeVisible = pageNoticeButton.Visible;
        try
        {
            foreach (bool compact in new [] { false, true })
            {
                Size = compact ? MinimumSize : original;
                foreach (bool showNotice in new [] { false, true })
                {
                pageNoticeButton.Visible = showNotice;
                PerformLayout(); await Task.Delay(200);
                bool visible = true;
                foreach (Control control in refreshButton.Parent.Controls)
                {
                    if (!control.Visible) continue;
                    Rectangle bounds = RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                    visible &= ClientRectangle.Contains(bounds) && control.Parent.ClientRectangle.Contains(control.Bounds);
                }
                await RenderShellPreview((compact ? "desktop-compact" : "desktop") + (showNotice ? "-notice" : "") + "-preview.png");
                Assert(visible && view.ClientSize.Width >= 600 && view.ClientSize.Height >= 300,
                    "desktop layout: all visible execution actions and browser fit " + (compact ? "minimum" : "default") + " window size" + (showNotice ? " with notice" : ""));
                }
            }
        }
        finally { pageNoticeButton.Visible = noticeVisible; Size = original; }
    }
}
