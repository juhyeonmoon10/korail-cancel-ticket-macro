using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class RefreshSettingsDialog : Form
{
    // Keep editable fields as direct children for accessibility and dialog tests.
    readonly TextBox interval = new TextBox { Left = 28, Top = 152, Width = 304, MaxLength = 0, AccessibleName = "조회 간격", TabIndex = 0 };
    readonly TextBox count = new TextBox { Left = 368, Top = 152, Width = 324, MaxLength = 0, AccessibleName = "추가 조회 횟수", TabIndex = 1 };
    readonly CheckBox unlimited = new CheckBox { Left = 368, Top = 187, Width = 324, Height = 28, Text = "횟수 제한 없이 반복", Name = "refreshUnlimited", AccessibleName = "횟수 제한 없이 반복", TabIndex = 2 };
    readonly TextBox jitter = new TextBox { Left = 28, Top = 258, Width = 140, MaxLength = 0, AccessibleName = "랜덤 지연 비율", TabIndex = 3 };
    readonly Label range = new Label { Left = 368, Top = 258, Width = 324, Height = 50, AccessibleName = "실제 대기 범위" };
    readonly Label error = new Label { Left = 28, Top = 576, Width = 664, Height = 50, AccessibleName = "입력 오류" };
    readonly CheckBox book = new CheckBox { Left = 28, Top = 380, Width = 664, Height = 32, Text = "좌석이 나오면 선택한 열차 예매까지 진행", Name = "bookWhenAvailable", AccessibleName = "좌석이 나오면 선택한 열차 예매까지 진행", TabIndex = 4 };
    readonly ComboBox completionMode = new ComboBox { Left = 52, Top = 443, Width = 640, DropDownStyle = ComboBoxStyle.DropDownList, Name = "bookingCompletionMode", AccessibleName = "예매 완료 조건", TabIndex = 5 };
    readonly Button start = new Button { Left = 516, Top = 642, Width = 176, Height = 42, Text = "자동 조회 시작", AccessibleName = "자동 조회 시작", TabIndex = 6 };

    public RefreshOptions SelectedOptions { get; private set; }
    public bool BookWhenAvailable { get { return book.Checked; } }

    public RefreshSettingsDialog(RefreshOptions current, Font font, bool queueMode = false)
    {
        UiTheme.StyleForm(this);
        Text = "마우스 자동 조회"; ClientSize = new Size(720, 708);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; BackColor = UiTheme.Surface;
        var heading = new Label { Left = 28, Top = 24, Width = 664, Height = 36, Text = "자동 조회 설정", Font = new Font(Font.FontFamily, 19, FontStyle.Bold) };
        var intro = Help("같은 조건을 다시 조회합니다. 간격과 반복 횟수, 예매 완료 조건을 정하세요.", 28, 68, 664, 40);
        var intervalLabel = Caption("조회 간격 · 초", 28, 122, 304);
        var countLabel = Caption("추가 조회 횟수 · 회", 368, 122, 324);
        var intervalHelp = Help("결과 확인 후 기다리는 시간", 28, 187, 304, 24);
        var jitterLabel = Caption("랜덤 지연 · ±%", 28, 228, 304);
        var rangeLabel = Caption("실제 대기 범위", 368, 228, 324);
        var jitterHelp = Help("0~100%\n0%는 고정 대기", 184, 254, 164, 52);
        var delayNote = Help("횟수 제한 없이 반복: 예매 완료 조건까지 진행하며, 조회 전용이면 직접 중지하세요.\n실행 중 하단에 누적 추가 조회 횟수를 표시합니다.", 28, 314, 664, 48);
        var divider = new Label { Left = 28, Top = 366, Width = 664, Height = 1, BackColor = UiTheme.Border };
        var completionLabel = Caption("예매 완료 조건", 52, 414, 640);
        var note = Help("예매를 체크하지 않으면 조회만 합니다. 결제는 진행하지 않습니다.\n예매하려면 열차·좌석 등급·확인란과 텔레그램 설정을 먼저 완료하세요.", 52, 486, 640, 52);
        var stopNote = Help("F8 중지   ·   도우미 직접 조작은 일시정지   ·   차단·인증 안내 시 중지", 28, 546, 664, 24);
        var cancel = new Button { Left = 400, Top = 642, Width = 104, Height = 42, Text = "취소", DialogResult = DialogResult.Cancel, TabIndex = 7 };
        UiTheme.StyleInput(interval); UiTheme.StyleInput(count); UiTheme.StyleInput(jitter); UiTheme.StyleInput(completionMode);
        UiTheme.StyleButton(start, "primary"); UiTheme.StyleButton(cancel);
        range.ForeColor = UiTheme.Accent; range.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        error.ForeColor = UiTheme.Danger;
        Controls.AddRange(new Control[] { heading, intro, intervalLabel, interval, intervalHelp, countLabel, count, unlimited,
            jitterLabel, jitter, jitterHelp, rangeLabel, range, delayNote, divider, book, completionLabel, completionMode, note, stopNote, error, start, cancel });
        AcceptButton = start; CancelButton = cancel;
        interval.Text = current.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        count.Text = current.Limit.ToString(CultureInfo.InvariantCulture);
        jitter.Text = current.JitterPercent.ToString(CultureInfo.InvariantCulture);
        unlimited.Checked = current.Unlimited; count.Enabled = !unlimited.Checked;
        completionMode.Items.AddRange(new object[] { "선택한 열차 중 하나라도 예약되면 종료", "선택한 열차를 모두 예약하면 종료" });
        completionMode.SelectedIndex = current.CompletionMode == BookingCompletionMode.All ? 1 : 0;
        completionMode.Enabled = book.Checked;
        interval.TextChanged += delegate { ValidateInputs(); };
        count.TextChanged += delegate { ValidateInputs(); };
        jitter.TextChanged += delegate { ValidateInputs(); };
        unlimited.CheckedChanged += delegate { count.Enabled = !unlimited.Checked; ValidateInputs(); };
        completionMode.SelectedIndexChanged += delegate { ValidateInputs(); };
        book.CheckedChanged += delegate { completionMode.Enabled = book.Checked; };
        start.Click += delegate {
            RefreshOptions selected;
            if (!TryRead(out selected)) return;
            SelectedOptions = selected; DialogResult = DialogResult.OK;
        };
        ValidateInputs();
        if (queueMode)
        {
            Text = "예약 순서에 추가"; heading.Text = "이 예약의 조회 설정";
            intro.Text = "날짜·구간·선택한 열차와 함께 저장합니다. 완료 조건에 따라 다음 예약으로 이어집니다.";
            book.Checked = true; book.Enabled = false;
            start.Text = "예약 순서에 추가"; start.AccessibleName = start.Text;
        }
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
        BookingCompletionMode selectedMode = completionMode.SelectedIndex == 1 ? BookingCompletionMode.All : BookingCompletionMode.Any;
        bool valid = RefreshOptions.TryParse(interval.Text, count.Text, jitter.Text, unlimited.Checked, selectedMode, out options, out message);
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
