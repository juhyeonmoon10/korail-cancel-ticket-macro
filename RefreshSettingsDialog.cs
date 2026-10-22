using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal sealed class RefreshSettingsDialog : Form
{
    readonly TextBox interval = new TextBox { Left = 155, Top = 100, Width = 125, MaxLength = 0, AccessibleName = "조회 간격" };
    readonly TextBox count = new TextBox { Left = 475, Top = 100, Width = 150, MaxLength = 0, AccessibleName = "추가 조회 횟수" };
    readonly TextBox jitter = new TextBox { Left = 155, Top = 150, Width = 125, MaxLength = 0, AccessibleName = "랜덤 지연 비율" };
    readonly Label range = new Label { Left = 20, Top = 204, Width = 620, Height = 30, AccessibleName = "실제 대기 범위" };
    readonly Label error = new Label { Left = 20, Top = 406, Width = 620, Height = 50, ForeColor = Color.Firebrick, AccessibleName = "입력 오류" };
    readonly CheckBox book = new CheckBox { Left = 20, Top = 304, Width = 620, Height = 36, Text = "미리 선택한 열차의 좌석이 나오면 예매까지 진행 (결제 제외)" };
    readonly Button start = new Button { Left = 425, Top = 470, Width = 100, Height = 35, Text = "시작", AccessibleName = "자동 조회 시작" };

    public RefreshOptions SelectedOptions { get; private set; }
    public bool BookWhenAvailable { get { return book.Checked; } }

    public RefreshSettingsDialog(RefreshOptions current, Font font)
    {
        Text = "마우스 자동 조회"; ClientSize = new Size(660, 520);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; Font = font;
        var info = new Label { Left = 20, Top = 18, Width = 620, Height = 70,
            Text = "입력한 간격과 횟수대로 같은 조건을 마우스로 조회합니다.\n총 실행시간 제한 없음 · F8 중지 · 직접 입력·창 전환은 일시정지\n차단·인증 안내나 결과 확인 실패 시에는 중지합니다." };
        var intervalLabel = new Label { Left = 20, Top = 105, Width = 130, Text = "조회 간격 (초)" };
        var countLabel = new Label { Left = 325, Top = 105, Width = 150, Text = "추가 조회 횟수" };
        var jitterLabel = new Label { Left = 20, Top = 155, Width = 130, Text = "랜덤 지연 (±%)" };
        var bounds = new Label { Left = 300, Top = 150, Width = 325, Height = 50,
            Text = "간격·횟수: 1~2147483647 정수\n랜덤 지연: 0~100% 정수" };
        var delayNote = new Label { Left = 20, Top = 244, Width = 620, Height = 52,
            Text = "간격은 이전 조회 결과를 확인한 뒤 기다리는 시간입니다.\n0%는 고정 대기 · 요청 중에는 추가 조회하지 않습니다." };
        var note = new Label { Left = 20, Top = 346, Width = 620, Height = 52,
            Text = "체크하지 않으면 조회만 합니다.\n예매하려면 열차·등급·확인란과 텔레그램 설정을 완료하세요." };
        var cancel = new Button { Left = 535, Top = 470, Width = 100, Height = 35, Text = "취소", DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { info, intervalLabel, interval, countLabel, count, jitterLabel, jitter, bounds, range, delayNote, book, note, error, start, cancel });
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
        range.Text = "실제 대기: " + policy.MinimumSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "~" + policy.MaximumSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "초";
    }
}
