using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class RefreshSettingsDialog : Form
{
    // Keep editable fields as direct children for accessibility and dialog tests.
    readonly TextBox interval = new TextBox { Left = 28, Top = 152, Width = 304, MaxLength = 0, AccessibleName = "조회 간격", TabIndex = 0 };
    readonly TextBox count = new TextBox { Left = 368, Top = 152, Width = 324, MaxLength = 0, AccessibleName = "추가 조회 횟수", TabIndex = 1 };
    readonly TextBox jitter = new TextBox { Left = 28, Top = 242, Width = 140, MaxLength = 0, AccessibleName = "랜덤 지연 비율", TabIndex = 2 };
    readonly Label range = new Label { Left = 368, Top = 242, Width = 324, Height = 50, AccessibleName = "실제 대기 범위" };
    readonly Label error = new Label { Left = 28, Top = 488, Width = 664, Height = 50, AccessibleName = "입력 오류" };
    readonly CheckBox book = new CheckBox { Left = 28, Top = 366, Width = 664, Height = 42, Text = "좌석이 나오면 선택한 열차 예매까지 진행", TabIndex = 3 };
    readonly Button start = new Button { Left = 516, Top = 554, Width = 176, Height = 42, Text = "자동 조회 시작", AccessibleName = "자동 조회 시작", TabIndex = 4 };

    public RefreshOptions SelectedOptions { get; private set; }
    public bool BookWhenAvailable { get { return book.Checked; } }

    public RefreshSettingsDialog(RefreshOptions current, Font font)
    {
        UiTheme.StyleForm(this);
        Text = "마우스 자동 조회"; ClientSize = new Size(720, 620);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; BackColor = UiTheme.Surface;
        var heading = new Label { Left = 28, Top = 24, Width = 664, Height = 36, Text = "자동 조회 설정", Font = new Font(Font.FontFamily, 19, FontStyle.Bold) };
        var intro = Help("같은 조건을 마우스로 다시 조회합니다. 간격과 횟수를 직접 정하세요.", 28, 68, 664, 40);
        var intervalLabel = Caption("조회 간격 · 초", 28, 122, 304);
        var countLabel = Caption("추가 조회 횟수 · 회", 368, 122, 324);
        var intervalHelp = Help("결과 확인 후 기다리는 시간", 28, 187, 304, 24);
        var countHelp = Help("총 실행시간 제한 없음", 368, 187, 324, 24);
        var jitterLabel = Caption("랜덤 지연 · ±%", 28, 212, 304);
        var rangeLabel = Caption("실제 대기 범위", 368, 212, 324);
        var jitterHelp = Help("0~100%\n0%는 고정 대기", 184, 238, 164, 52);
        var delayNote = Help("조회 중에는 다음 요청을 보내지 않습니다.\n간격·횟수는 1~2147483647 정수로 입력하세요.", 28, 296, 664, 52);
        var divider = new Label { Left = 28, Top = 354, Width = 664, Height = 1, BackColor = UiTheme.Border };
        var note = Help("체크하지 않으면 조회만 합니다. 예매는 결제 단계 전까지 진행합니다.\n예매하려면 열차·좌석 등급·확인란과 텔레그램 설정을 먼저 완료하세요.", 52, 410, 640, 52);
        var stopNote = Help("F8 중지   ·   직접 입력·창 전환은 일시정지   ·   차단·인증 안내 시 중지", 28, 465, 664, 24);
        var cancel = new Button { Left = 400, Top = 554, Width = 104, Height = 42, Text = "취소", DialogResult = DialogResult.Cancel, TabIndex = 5 };
        UiTheme.StyleInput(interval); UiTheme.StyleInput(count); UiTheme.StyleInput(jitter);
        UiTheme.StyleButton(start, "primary"); UiTheme.StyleButton(cancel);
        range.ForeColor = UiTheme.Accent; range.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        error.ForeColor = UiTheme.Danger;
        Controls.AddRange(new Control[] { heading, intro, intervalLabel, interval, intervalHelp, countLabel, count, countHelp,
            jitterLabel, jitter, jitterHelp, rangeLabel, range, delayNote, divider, book, note, stopNote, error, start, cancel });
        AcceptButton = start; CancelButton = cancel;
        interval.Text = current.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        count.Text = current.Limit.ToString(CultureInfo.InvariantCulture);
        jitter.Text = current.JitterPercent.ToString(CultureInfo.InvariantCulture);
        interval.TextChanged += delegate { ValidateInputs(); };
        count.TextChanged += delegate { ValidateInputs(); };
        jitter.TextChanged += delegate { ValidateInputs(); };
        start.Click += delegate {
            RefreshOptions selected;
            if (!TryRead(out selected)) return;
            SelectedOptions = selected; DialogResult = DialogResult.OK;
        };
        ValidateInputs();
    }

    static Label Caption(string text, int left, int top, int width)
    {
        return new Label { Text = text, Left = left, Top = top, Width = width, Height = 26, ForeColor = UiTheme.Text };
    }
    static Label Help(string text, int left, int top, int width, int height)
    {
        return new Label { Text = text, Left = left, Top = top, Width = width, Height = height, ForeColor = UiTheme.Muted };
    }
    bool TryRead(out RefreshOptions options)
    {
        string message;
        bool valid = RefreshOptions.TryParse(interval.Text, count.Text, jitter.Text, out options, out message);
        error.Text = valid ? "" : message;
        return valid;
    }

    void ValidateInputs()
    {
        RefreshOptions options;
        bool valid = TryRead(out options); start.Enabled = valid;
        if (!valid) { range.Text = "실제 대기: 입력값을 확인하세요."; return; }
        var policy = new RefreshDelayPolicy(options.IntervalSeconds, options.JitterPercent);
        string minimum = policy.MinimumSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        string maximum = policy.MaximumSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        string preview = "실제 대기: " + minimum + "~" + maximum + "초";
        if (TextRenderer.MeasureText(preview, range.Font).Width > range.ClientSize.Width)
            preview = "실제 대기: " + minimum + "~\n" + maximum + "초";
        range.Text = preview;
    }
}
