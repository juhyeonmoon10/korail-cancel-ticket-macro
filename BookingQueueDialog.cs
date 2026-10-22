using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal enum BookingQueueDialogAction { None, AddCurrent, StartQueue, BookCurrent, MoveUp, MoveDown, Remove, Retry, History }

// The dialog owns presentation only. The host applies mutations through
// ActionHandler and returns a fresh snapshot. StartQueue/BookCurrent instead
// close with OK so native mouse execution starts after the modal window closes.
internal sealed class BookingQueueDialog : Form
{
    readonly DataGridView table = new DataGridView();
    readonly Label summary = new Label();
    readonly Label message = new Label();
    readonly TextBox detail = new TextBox();
    readonly Button add;
    readonly Button book;
    readonly Button history;
    readonly Button up;
    readonly Button down;
    readonly Button remove;
    readonly Button retry;
    readonly Button start;
    readonly Button close;
    readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };
    List<BookingQueueJob> jobs = new List<BookingQueueJob>();
    bool busy;
    Func<BookingQueueDialogAction, string, Task<IList<BookingQueueJob>>> actionHandler;

    public BookingQueueDialogAction RequestedAction { get; private set; }
    public string SelectedJobId { get; private set; }
    public Func<BookingQueueDialogAction, string, Task<IList<BookingQueueJob>>> ActionHandler
    {
        get { return actionHandler; }
        set { actionHandler = value; UpdateActions(); }
    }

    public BookingQueueDialog(IList<BookingQueueJob> snapshot)
    {
        UiTheme.StyleForm(this);
        Text = "예약 관리"; ClientSize = new Size(920, 640); MinimumSize = new Size(800, 580);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(24, 20, 24, 20), Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 38f, 46f, 46f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 44f, 62f, 28f, 52f }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "예약 관리", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        summary.Dock = DockStyle.Fill; summary.ForeColor = UiTheme.Muted; summary.Margin = Padding.Empty;
        summary.AccessibleName = "예약 순서 안내"; layout.Controls.Add(summary, 0, 1);

        var preparation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        add = Button("현재 선택을 순서에 추가", 240, "secondary", "현재 선택 추가");
        book = Button("지금 한 번 예약", 176, "secondary", "지금 한 번 예약");
        history = Button("예약 기록", 116, "quiet", "예약 기록");
        preparation.Controls.AddRange(new Control[] { add, book, history }); layout.Controls.Add(preparation, 0, 2);

        table.Dock = DockStyle.Fill; table.Margin = new Padding(0, 0, 0, 6);
        table.BackgroundColor = UiTheme.Surface; table.BorderStyle = BorderStyle.None;
        table.GridColor = UiTheme.Border; table.RowHeadersVisible = false;
        table.AllowUserToAddRows = table.AllowUserToDeleteRows = table.AllowUserToResizeRows = false;
        table.ReadOnly = true; table.MultiSelect = false; table.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; table.RowTemplate.Height = 44;
        table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; table.ColumnHeadersHeight = 36;
        table.EnableHeadersVisualStyles = false; table.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(234, 239, 248), ForeColor = UiTheme.Text, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(5, 0, 0, 0) };
        table.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, SelectionBackColor = Color.FromArgb(223, 233, 254), SelectionForeColor = UiTheme.Text, Padding = new Padding(5, 0, 0, 0), WrapMode = DataGridViewTriState.False };
        table.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
        table.AccessibleName = "예약 순서 목록";
        AddColumn("order", "순서", 60); AddColumn("state", "상태", 92); AddColumn("date", "날짜", 112); AddColumn("route", "구간", 164);
        var trains = new DataGridViewTextBoxColumn { Name = "trains", HeaderText = "후보 열차 · 한 편만 예약", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 190, SortMode = DataGridViewColumnSortMode.NotSortable };
        table.Columns.Add(trains); layout.Controls.Add(table, 0, 3);

        var edit = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        up = Button("↑", 44, "secondary", "예약 순서 위로"); down = Button("↓", 44, "secondary", "예약 순서 아래로");
        remove = Button("목록에서 제거", 148, "secondary", "예약 순서 제거"); retry = Button("결과 확인 후 다시 대기", 240, "secondary", "예약 결과 확인 후 다시 대기");
        edit.Controls.AddRange(new Control[] { up, down, remove, retry }); layout.Controls.Add(edit, 0, 4);
        tips.SetToolTip(up, "선택한 대기 항목을 한 칸 위로 이동합니다."); tips.SetToolTip(down, "선택한 대기 항목을 한 칸 아래로 이동합니다.");
        tips.SetToolTip(remove, "대기 또는 완료 항목을 이 목록에서만 제거합니다. 코레일 예약은 취소하지 않습니다.");
        tips.SetToolTip(retry, "공식 예약 내역을 확인한 뒤, 확인이 필요한 항목을 다시 대기시킵니다.");

        detail.Dock = DockStyle.Fill; detail.Multiline = true; detail.ReadOnly = true; detail.ScrollBars = ScrollBars.Vertical;
        detail.BorderStyle = BorderStyle.None; detail.BackColor = UiTheme.Background; detail.ForeColor = UiTheme.Muted;
        detail.Margin = new Padding(0, 3, 0, 5); detail.AccessibleName = "선택한 예약 상세";
        layout.Controls.Add(detail, 0, 5);
        message.Dock = DockStyle.Fill; message.ForeColor = UiTheme.Muted; message.Margin = Padding.Empty; message.AutoEllipsis = true; message.AccessibleName = "예약 관리 상태";
        layout.Controls.Add(message, 0, 6);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        start = Button("예약 순서 시작", 174, "primary", "예약 순서 시작"); close = Button("닫기", 100, "secondary", "예약 관리 닫기");
        start.Margin = new Padding(8, 0, 0, 0); close.Margin = Padding.Empty;
        footer.Controls.Add(start); footer.Controls.Add(close); layout.Controls.Add(footer, 0, 7);
        Controls.Add(layout); CancelButton = close;

        add.Click += async delegate { await RunAction(BookingQueueDialogAction.AddCurrent); };
        history.Click += async delegate { await RunAction(BookingQueueDialogAction.History); };
        up.Click += async delegate { await RunAction(BookingQueueDialogAction.MoveUp); };
        down.Click += async delegate { await RunAction(BookingQueueDialogAction.MoveDown); };
        remove.Click += async delegate { await RunAction(BookingQueueDialogAction.Remove); };
        retry.Click += async delegate { await RunAction(BookingQueueDialogAction.Retry); };
        start.Click += delegate { Finish(BookingQueueDialogAction.StartQueue); };
        book.Click += delegate { Finish(BookingQueueDialogAction.BookCurrent); };
        close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        table.SelectionChanged += delegate { SelectionChanged(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        FormClosed += delegate { tips.Dispose(); };
        SetJobs(snapshot);
    }

    Button Button(string text, int width, string variant, string accessibleName)
    {
        var button = new Button { Text = text, Font = Font, Width = width, Height = 36, Margin = new Padding(0, 0, 8, 0), AccessibleName = accessibleName };
        UiTheme.StyleButton(button, variant); button.Padding = new Padding(8, 3, 8, 3);
        // Measure with the actual dialog font before the button joins a parent.
        // DPI-aware glyphs, including the arrow buttons, need their full width
        // plus padding; fixed widths alone can clip the last Korean syllable.
        Size measured = TextRenderer.MeasureText(button.Text, button.Font);
        button.Width = Math.Max(width, measured.Width + button.Padding.Horizontal + 8);
        button.Height = Math.Max(36, measured.Height + button.Padding.Vertical + 4);
        return button;
    }
    void AddColumn(string name, string title, int width)
    {
        table.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, Width = width, MinimumWidth = width, SortMode = DataGridViewColumnSortMode.NotSortable });
    }
    public static string StateLabel(BookingQueueState state)
    {
        switch (state)
        {
            case BookingQueueState.Pending: return "대기";
            case BookingQueueState.Running: return "진행 중";
            case BookingQueueState.Completed: return "예약 완료";
            case BookingQueueState.NeedsReview: return "결과 확인";
            default: return "확인 필요";
        }
    }
    public void SetJobs(IList<BookingQueueJob> snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException("snapshot");
        string previous = SelectedJobId;
        jobs = snapshot.Select(job => job.Copy()).ToList();
        table.Rows.Clear();
        foreach (var job in jobs)
        {
            var first = job.Targets[0];
            string trains = String.Join(", ", job.Targets.Select(t => t.Train + " " + t.Departure));
            int index = table.Rows.Add((table.Rows.Count + 1).ToString(CultureInfo.InvariantCulture), StateLabel(job.State), first.Date, first.DepartureStation + " → " + first.ArrivalStation, trains);
            var row = table.Rows[index]; row.Tag = job.Id;
            row.Cells[4].ToolTipText = trains + "\n" + first.Grade + " · 1명 · 후보 중 한 편 성공 시 이 항목 완료";
            if (job.State == BookingQueueState.Completed) row.Cells[1].Style.ForeColor = Color.FromArgb(30, 111, 78);
            else if (job.State == BookingQueueState.NeedsReview) row.Cells[1].Style.ForeColor = UiTheme.Danger;
        }
        table.ClearSelection();
        int selected = jobs.FindIndex(j => j.Id == previous);
        if (selected < 0) selected = jobs.FindIndex(j => j.State == BookingQueueState.Pending || j.State == BookingQueueState.NeedsReview);
        if (selected < 0 && jobs.Count > 0) selected = 0;
        if (selected >= 0) { table.Rows[selected].Selected = true; table.CurrentCell = table.Rows[selected].Cells[0]; }
        summary.Text = "한 줄이 예약 한 건입니다. 후보 중 한 편을 예약하면 다음 줄로 진행합니다.\n대기 " + jobs.Count(j => j.State == BookingQueueState.Pending) + "건  ·  완료 " + jobs.Count(j => j.State == BookingQueueState.Completed) + "건  ·  전체 " + jobs.Count + "/20건  ·  결제는 직접 진행하세요.";
        SelectionChanged();
        if (jobs.Count == 0) SetMessage("코레일에서 열차를 선택한 뒤 ‘현재 선택을 순서에 추가’를 누르세요.", false);
        else if (jobs.Any(j => j.State == BookingQueueState.NeedsReview)) SetMessage("‘결과 확인’ 항목이 있습니다. 공식 예약 내역을 확인한 뒤 진행하세요.", true);
        else SetMessage("조회 간격과 횟수는 각 항목에 저장됩니다. 하나만 예약하려면 한 줄만 추가하세요.", false);
    }
    BookingQueueJob SelectedJob() { return jobs.FirstOrDefault(j => j.Id == SelectedJobId); }
    void SelectionChanged()
    {
        SelectedJobId = table.SelectedRows.Count == 1 ? table.SelectedRows[0].Tag as string : null;
        var selected = SelectedJob();
        if (selected == null) detail.Text = "선택한 예약이 없습니다.";
        else
        {
            var target = selected.Targets[0];
            detail.Text = target.Date + " · " + target.DepartureStation + " → " + target.ArrivalStation + " · " + target.Grade + " · 1명\r\n"
                + "조회 " + selected.IntervalSeconds.ToString(CultureInfo.InvariantCulture) + "초 / " + selected.Limit.ToString(CultureInfo.InvariantCulture) + "회 / ±" + selected.JitterPercent.ToString(CultureInfo.InvariantCulture) + "% · 후보 " + selected.Targets.Count + "편 중 한 편만 예약";
        }
        UpdateActions();
    }
    void UpdateActions()
    {
        // During construction buttons may not have been assigned yet.
        if (add == null || start == null) return;
        var selected = SelectedJob();
        int index = selected == null ? -1 : jobs.FindIndex(j => j.Id == selected.Id);
        bool canEdit = !busy && actionHandler != null;
        add.Enabled = canEdit && jobs.Count < BookingQueueStore.MaximumJobs;
        history.Enabled = canEdit;
        up.Enabled = canEdit && selected != null && selected.State == BookingQueueState.Pending && index > 0;
        down.Enabled = canEdit && selected != null && selected.State == BookingQueueState.Pending && index >= 0 && index < jobs.Count - 1;
        remove.Enabled = canEdit && selected != null && (selected.State == BookingQueueState.Pending || selected.State == BookingQueueState.Completed);
        retry.Enabled = canEdit && selected != null && selected.State == BookingQueueState.NeedsReview;
        bool unsettled = jobs.Any(j => j.State == BookingQueueState.Running || j.State == BookingQueueState.NeedsReview);
        start.Enabled = !busy && !unsettled && jobs.Any(j => j.State == BookingQueueState.Pending);
        book.Enabled = !busy && !unsettled;
        close.Enabled = !busy;
    }
    async Task RunAction(BookingQueueDialogAction action)
    {
        if (busy || actionHandler == null) return;
        string id = SelectedJobId;
        busy = true; UpdateActions(); SetMessage("처리 중입니다…", false);
        try
        {
            var snapshot = await actionHandler(action, id);
            if (IsDisposed) return;
            if (snapshot != null) SetJobs(snapshot);
            else SetMessage("준비되었습니다.", false);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) SetMessage(ex is InvalidOperationException || ex is ArgumentException ? ex.Message : "작업을 완료하지 못했습니다. 저장 폴더와 예약 기록을 확인하세요.", true);
        }
        finally { busy = false; if (!IsDisposed) UpdateActions(); }
    }
    void Finish(BookingQueueDialogAction action)
    {
        if (busy) return;
        RequestedAction = action; DialogResult = DialogResult.OK; Close();
    }
    public void SetMessage(string text, bool error)
    {
        message.Text = text ?? ""; message.ForeColor = error ? UiTheme.Danger : UiTheme.Muted; tips.SetToolTip(message, message.Text);
    }
    public bool LayoutFits()
    {
        PerformLayout();
        return new[] { add, book, history, up, down, remove, retry, start, close }.All(button => button.Parent.ClientRectangle.Contains(button.Bounds)
            && TextRenderer.MeasureText(button.Text, button.Font).Width + button.Padding.Horizontal + 4 <= button.ClientSize.Width)
            && table.ClientSize.Width >= 600 && table.ClientSize.Height >= 90;
    }
}
