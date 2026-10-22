using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class TrainNumberSelection
{
    // Keep the persisted TrainNumber string compatible with older settings.
    // Consumers share this numeric normalization for filtering and validation.
    public static List<int> Parse(string value)
    {
        var result = new List<int>();
        if (String.IsNullOrWhiteSpace(value)) return result;
        var seen = new HashSet<int>();
        foreach (string item in value.Split(','))
        {
            string token = item.Trim();
            if (!Regex.IsMatch(token, @"\A[0-9]{1,5}\z"))
                throw new FormatException("열차번호는 쉼표로 구분한 1~5자리 숫자로 입력하세요. 예: 505,541,561. 빈 항목은 넣을 수 없습니다.");
            int number = Int32.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number == 0) throw new FormatException("열차번호는 0보다 커야 합니다.");
            if (!seen.Add(number)) continue;
            if (result.Count == 20) throw new FormatException("열차번호는 중복을 제외하고 최대 20개까지 지정할 수 있습니다.");
            result.Add(number);
        }
        return result;
    }
}

internal sealed class JourneySettings
{
    public bool Enabled { get; set; }
    public string DepartureStation { get; set; }
    public string ArrivalStation { get; set; }
    public string Date { get; set; }
    public string FromTime { get; set; }
    public string ToTime { get; set; }
    public string TrainNumber { get; set; }
    public string Grade { get; set; }

    public JourneySettings()
    {
        DepartureStation = ""; ArrivalStation = "";
        Date = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FromTime = "00:00"; ToTime = "23:59"; TrainNumber = ""; Grade = "일반실";
    }
    public JourneySettings Copy() { return (JourneySettings)MemberwiseClone(); }
    public string Validate()
    {
        if (!Enabled) return null;
        if (!ValidStation(DepartureStation) || !ValidStation(ArrivalStation)) return "출발역과 도착역을 각각 입력하세요. (최대 40자)";
        if (DepartureStation.Trim() == ArrivalStation.Trim()) return "출발역과 도착역을 다르게 지정하세요.";
        DateTime date, from, to;
        if (!DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return "승차 날짜를 확인하세요.";
        if (!DateTime.TryParseExact(FromTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out from) ||
            !DateTime.TryParseExact(ToTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)) return "출발 시간대를 확인하세요.";
        if (from.TimeOfDay > to.TimeOfDay) return "출발 시간대의 시작은 끝보다 늦을 수 없습니다. 날짜별로 지정하세요.";
        try { TrainNumberSelection.Parse(TrainNumber); }
        catch (FormatException ex) { return ex.Message; }
        if (Grade != "일반실" && Grade != "특실" && Grade != "우등실") return "좌석 등급을 선택하세요.";
        return null;
    }
    static bool ValidStation(string value)
    {
        return !String.IsNullOrWhiteSpace(value) && value.Trim().Length <= 40 && !Regex.IsMatch(value, @"[\x00-\x1F\x7F]");
    }
}

internal sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; }
    public string ChatId { get; set; }
    public TelegramSettings() { BotToken = ""; ChatId = ""; }
    public TelegramSettings Copy() { return (TelegramSettings)MemberwiseClone(); }
    // Onboarding checks stored credentials independently of the notification toggle.
    // Turning notifications off must not make a configured user enter them again.
    [ScriptIgnore]
    public bool HasCredentials
    {
        get { var required = Copy(); required.Enabled = true; return required.Validate() == null; }
    }
    public string Validate()
    {
        if (!Enabled) return null;
        if (String.IsNullOrWhiteSpace(BotToken) || !Regex.IsMatch(BotToken.Trim(), @"^[0-9]{5,20}:[A-Za-z0-9_-]{20,200}$")) return "텔레그램 BotFather에서 발급받은 봇 토큰을 확인하세요.";
        long chat;
        if (String.IsNullOrWhiteSpace(ChatId) || !Regex.IsMatch(ChatId.Trim(), @"^-?[0-9]{1,20}$") || !Int64.TryParse(ChatId.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out chat) || chat == 0) return "Chat ID는 0이 아닌 숫자로 입력하세요. 그룹 ID의 음수 기호도 포함하세요.";
        return null;
    }
}

internal sealed class AppSettings
{
    public JourneySettings Journey { get; set; }
    public TelegramSettings Telegram { get; set; }
    public string LoadWarning { get; private set; }
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KorailOneHelper.Settings.Token.v1");
    public AppSettings() { Journey = new JourneySettings(); Telegram = new TelegramSettings(); }
    public AppSettings Copy() { return new AppSettings { Journey = Journey.Copy(), Telegram = Telegram.Copy() }; }

    // This disk-only DTO deliberately has no plaintext BotToken property.
    public sealed class StoredSettings
    {
        public int Version { get; set; }
        public JourneySettings Journey { get; set; }
        public StoredTelegram Telegram { get; set; }
    }
    public sealed class StoredTelegram
    {
        public bool Enabled { get; set; }
        public string ChatId { get; set; }
        public string BotTokenProtected { get; set; }
    }
    public static AppSettings Load(string path)
    {
        var result = new AppSettings();
        if (!File.Exists(path)) return result;
        try
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
            var saved = new JavaScriptSerializer().Deserialize<StoredSettings>(File.ReadAllText(path, Encoding.UTF8));
            if (saved == null || saved.Version != 1) throw new InvalidDataException();
            if (saved.Journey != null && saved.Journey.Validate() == null) result.Journey = saved.Journey;
            else result.LoadWarning = "저장된 열차 조건을 읽지 못했습니다. 설정에서 다시 지정하세요.";
            if (saved.Telegram != null)
            {
                result.Telegram.Enabled = saved.Telegram.Enabled;
                result.Telegram.ChatId = saved.Telegram.ChatId ?? "";
                if (!String.IsNullOrEmpty(saved.Telegram.BotTokenProtected))
                {
                    byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(saved.Telegram.BotTokenProtected), Entropy, DataProtectionScope.CurrentUser);
                    try { result.Telegram.BotToken = Encoding.UTF8.GetString(plain); }
                    finally { Array.Clear(plain, 0, plain.Length); }
                }
                if (result.Telegram.Validate() != null) throw new InvalidDataException();
            }
        }
        catch
        {
            result.Telegram = new TelegramSettings();
            result.LoadWarning = "저장된 설정 또는 봇 토큰을 읽지 못했습니다. 텔레그램 알림은 꺼졌습니다. 설정을 다시 확인하세요.";
        }
        return result;
    }
    public void Save(string path)
    {
        string issue = Journey.Validate() ?? Telegram.Validate();
        if (issue != null) throw new ArgumentException(issue);
        var stored = new StoredSettings { Version = 1, Journey = Journey.Copy(), Telegram = new StoredTelegram { Enabled = Telegram.Enabled, ChatId = (Telegram.ChatId ?? "").Trim(), BotTokenProtected = "" } };
        if (!String.IsNullOrWhiteSpace(Telegram.BotToken))
        {
            byte[] plain = Encoding.UTF8.GetBytes(Telegram.BotToken.Trim());
            try { stored.Telegram.BotTokenProtected = Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(stored), new UTF8Encoding(false));
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class TelegramNotifier
{
    public const string SuccessMessage = "텔레그램 알림을 보냈습니다.";
    public static Task<string> SendAsync(TelegramSettings settings, string text)
    {
        return SendAsync(settings, text, null);
    }
    public static Task<TelegramChatLookup> FindChatsAsync(string botToken)
    {
        return FindChatsAsync(botToken, null);
    }
    internal static async Task<TelegramChatLookup> FindChatsAsync(string botToken, HttpMessageHandler testHandler)
    {
        var credentials = new TelegramSettings { Enabled = true, BotToken = botToken, ChatId = "1" };
        string invalid = credentials.Validate();
        if (invalid != null) return new TelegramChatLookup { Error = invalid };
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var handler = testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            using (var client = new HttpClient(handler, false))
            {
                client.Timeout = TimeSpan.FromSeconds(15); client.MaxResponseContentBufferSize = 1048576;
                // Omitting offset leaves pending updates unconfirmed; do not change the bot's webhook or allowed_updates.
                using (var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "limit", "100" }, { "timeout", "0" } }))
                using (var response = await client.PostAsync("https://api.telegram.org/bot" + botToken.Trim() + "/getUpdates", content).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 409) return new TelegramChatLookup { Error = "이 봇이 웹훅이나 다른 프로그램에서 사용 중입니다. Chat ID를 직접 입력하거나 알림 전용 봇을 새로 만드세요." };
                    if (response.StatusCode == HttpStatusCode.Unauthorized) return new TelegramChatLookup { Error = "텔레그램 봇 토큰이 거부되었습니다. 다시 확인하세요." };
                    if ((int)response.StatusCode == 429) return new TelegramChatLookup { Error = "텔레그램 조회 횟수가 제한되었습니다. 잠시 후 다시 시도하세요." };
                    if (!response.IsSuccessStatusCode) return new TelegramChatLookup { Error = "텔레그램 대화를 조회하지 못했습니다. 토큰과 인터넷 연결을 확인하세요." };
                    var data = new JavaScriptSerializer { MaxJsonLength = 1048576 }.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    object ok, updates;
                    if (data == null || !data.TryGetValue("ok", out ok) || !(ok is bool) || !(bool)ok || !data.TryGetValue("result", out updates) || !(updates is IEnumerable))
                        return new TelegramChatLookup { Error = "텔레그램이 대화 조회를 수락하지 않았습니다. 봇 토큰을 확인하세요." };
                    var result = new TelegramChatLookup(); var seen = new HashSet<string>();
                    foreach (object item in (IEnumerable)updates)
                    {
                        var update = item as Dictionary<string, object>; object messageValue, chatValue, type, idValue;
                        if (update == null || (!update.TryGetValue("message", out messageValue) && !update.TryGetValue("edited_message", out messageValue))) continue;
                        var message = messageValue as Dictionary<string, object>;
                        if (message == null || !message.TryGetValue("chat", out chatValue)) continue;
                        var chat = chatValue as Dictionary<string, object>;
                        if (chat == null || !chat.TryGetValue("type", out type) || Convert.ToString(type, CultureInfo.InvariantCulture) != "private" || !chat.TryGetValue("id", out idValue)) continue;
                        long id;
                        if (!Int64.TryParse(Convert.ToString(idValue, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0) continue;
                        string chatId = id.ToString(CultureInfo.InvariantCulture);
                        if (!seen.Add(chatId)) continue;
                        string first = ChatLabel(chat, "first_name"), last = ChatLabel(chat, "last_name"), user = ChatLabel(chat, "username");
                        string name = (first + " " + last).Trim();
                        if (name.Length == 0) name = "개인 대화";
                        if (user.Length > 0) name += " (@" + user + ")";
                        result.Chats.Add(new TelegramChat { ChatId = chatId, DisplayName = name });
                    }
                    if (result.Chats.Count == 0) result.Error = "조회할 개인 대화가 없습니다. 텔레그램에서 이 봇에 /start를 보낸 뒤 다시 찾으세요. 그룹 Chat ID는 직접 입력하세요.";
                    return result;
                }
            }
        }
        catch (TaskCanceledException) { return new TelegramChatLookup { Error = "텔레그램 대화 조회 시간이 초과되었습니다. 잠시 후 다시 시도하세요." }; }
        catch { return new TelegramChatLookup { Error = "텔레그램 대화를 조회하지 못했습니다. 인터넷 연결과 봇 토큰을 확인하세요." }; }
    }
    static string ChatLabel(Dictionary<string, object> data, string key)
    {
        object value; if (!data.TryGetValue(key, out value) || !(value is string)) return "";
        string text = Regex.Replace((string)value, @"[\x00-\x1F\x7F]", " ").Trim();
        return text.Length <= 80 ? text : text.Substring(0, 80);
    }
    // A supplied handler is used only by local tests. Production always uses the fixed HTTPS endpoint.
    internal static async Task<string> SendAsync(TelegramSettings settings, string text, HttpMessageHandler testHandler)
    {
        if (settings == null || !settings.Enabled) return "텔레그램 알림이 꺼져 있습니다.";
        string invalid = settings.Validate();
        if (invalid != null) return invalid;
        if (String.IsNullOrWhiteSpace(text) || text.Length > 4096) return "알림 내용의 길이가 올바르지 않습니다.";
        var selected = settings.Copy();
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var handler = testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            using (var client = new HttpClient(handler, false))
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.MaxResponseContentBufferSize = 65536;
                var fields = new Dictionary<string, string> { { "chat_id", selected.ChatId.Trim() }, { "text", text } };
                using (var content = new FormUrlEncodedContent(fields))
                using (var response = await client.PostAsync("https://api.telegram.org/bot" + selected.BotToken.Trim() + "/sendMessage", content).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) return "텔레그램이 다른 주소로 연결을 요청하여 전송을 중지했습니다.";
                    if (response.StatusCode == HttpStatusCode.Unauthorized) return "텔레그램 봇 토큰이 거부되었습니다. 설정에서 다시 확인하세요.";
                    if (response.StatusCode == HttpStatusCode.Forbidden) return "텔레그램 전송 권한이 없습니다. 봇과 대화를 시작하고 차단 여부를 확인하세요.";
                    if ((int)response.StatusCode == 429) return "텔레그램 전송 횟수가 제한되었습니다. 잠시 후 직접 테스트하세요.";
                    if (!response.IsSuccessStatusCode) return "텔레그램 전송에 실패했습니다. 봇 토큰·Chat ID와 대화 시작 여부를 확인하세요.";
                    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    object ok;
                    if (data != null && data.TryGetValue("ok", out ok) && ok is bool && (bool)ok) return SuccessMessage;
                    return "텔레그램이 알림을 수락하지 않았습니다. 봇 토큰·Chat ID와 대화 시작 여부를 확인하세요.";
                }
            }
        }
        catch (TaskCanceledException) { return "텔레그램 전송 시간이 초과되어 도착 여부를 확인할 수 없습니다. 앱에서 예매 상태를 확인하세요."; }
        catch (HttpRequestException) { return "네트워크 오류로 텔레그램 알림의 도착 여부를 확인할 수 없습니다. 인터넷 연결과 앱의 예매 상태를 확인하세요."; }
        catch { return "텔레그램 알림을 처리하지 못했습니다. 설정에서 테스트 알림을 다시 확인하세요."; }
    }
}

internal sealed class TelegramChat
{
    public string ChatId { get; set; }
    public string DisplayName { get; set; }
    public override string ToString() { return DisplayName + "  ·  Chat ID " + ChatId; }
}
internal sealed class TelegramChatLookup
{
    public readonly List<TelegramChat> Chats = new List<TelegramChat>();
    public string Error { get; set; }
}
internal sealed class TelegramChatDialog : Form
{
    public TelegramChat SelectedChat { get; private set; }
    public TelegramChatDialog(IList<TelegramChat> chats)
    {
        UiTheme.StyleForm(this);
        Text = "알림을 받을 텔레그램 대화 선택"; ClientSize = new Size(660, 390);
        StartPosition = FormStartPosition.CenterParent; Padding = new Padding(24);
        ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        var heading = new Label { Dock = DockStyle.Top, Height = 64, ForeColor = UiTheme.Muted, Text = "알림을 받을 개인 대화를 선택하세요.\n선택한 대화의 Chat ID를 설정에 입력합니다." };
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, ItemHeight = 30 };
        foreach (var chat in chats) list.Items.Add(chat);
        list.SelectedIndex = -1;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(0, 18, 0, 0), FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "취소", Width = 100, Height = 40, DialogResult = DialogResult.Cancel };
        var select = new Button { Text = "이 대화 선택", Width = 132, Height = 40, Enabled = false };
        UiTheme.StyleButton(cancel); UiTheme.StyleButton(select, "primary");
        list.SelectedIndexChanged += delegate { select.Enabled = list.SelectedIndex >= 0; };
        select.Click += delegate { if (list.SelectedItem == null) return; SelectedChat = (TelegramChat)list.SelectedItem; DialogResult = DialogResult.OK; Close(); };
        actions.Controls.Add(select); actions.Controls.Add(cancel); Controls.Add(list); Controls.Add(heading); Controls.Add(actions); CancelButton = cancel; AcceptButton = select;
    }
}

internal sealed class SettingsDialog : Form
{
    readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(20, 11) };
    readonly CheckBox journeyEnabled = new CheckBox { Text = "저장한 열차 조건으로 대상 고르기", AutoSize = true };
    readonly TextBox departure = new TextBox { MaxLength = 40, AccessibleName = "출발역" };
    readonly TextBox arrival = new TextBox { MaxLength = 40, AccessibleName = "도착역" };
    readonly DateTimePicker date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" };
    readonly DateTimePicker from = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    readonly DateTimePicker to = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
    readonly TextBox train = new TextBox { MaxLength = 400, AccessibleName = "열차번호 목록 (쉼표 구분)" };
    readonly ComboBox grade = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox telegramEnabled = new CheckBox { Text = "예약 완료가 확인되면 텔레그램 알림 보내기", AutoSize = true };
    readonly TextBox token = new TextBox { UseSystemPasswordChar = true, MaxLength = 230, AccessibleName = "내 봇 토큰" };
    readonly TextBox chatId = new TextBox { MaxLength = 21, AccessibleName = "내 Chat ID" };
    readonly Label notice = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, AccessibleName = "설정 안내" };
    readonly Button test = new Button { Text = "테스트 알림 보내기", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    readonly Button findChat = new Button { Text = "Chat ID 찾기", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    readonly Button disconnect = new Button { Text = "연결 정보 지우기", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    readonly Button save = new Button { Text = "설정 저장", Width = 120, Height = 42 };
    bool working;
    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings initial) : this(initial, false) { }

    public SettingsDialog(AppSettings initial, bool openTelegram) : this(initial, openTelegram, false) { }

    public SettingsDialog(AppSettings initial, bool openTelegram, bool firstRun)
    {
        initial = initial ?? new AppSettings();
        UiTheme.StyleForm(this);
        Text = firstRun ? "처음 시작 · 내 텔레그램 연결" : "열차 선택 · 텔레그램 설정";
        ClientSize = new Size(760, 780); MinimumSize = new Size(720, 640);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        MaximizeBox = false; MinimizeBox = false;
        foreach (Control input in new Control[] { departure, arrival, date, from, to, train, grade, token, chatId }) UiTheme.StyleInput(input);
        UiTheme.StyleButton(test); UiTheme.StyleButton(findChat); UiTheme.StyleButton(disconnect, "quiet"); UiTheme.StyleButton(save, "primary");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = firstRun ? "예약 알림부터 연결하세요" : "나에게 맞게 설정하세요", Dock = DockStyle.Top, Height = 38, Font = new Font(Font.FontFamily, 18, FontStyle.Bold), ForeColor = UiTheme.Text });
        heading.Controls.Add(new Label { Text = firstRun ? "본인의 텔레그램 봇으로 예약 결과를 받아보세요." : "원하는 열차 조건과 예약 알림을 한 곳에서 관리합니다.", Dock = DockStyle.Bottom, Height = 32, ForeColor = UiTheme.Muted });
        var journeyPage = new TabPage("열차 선택") { BackColor = UiTheme.Surface, AutoScroll = true };
        var telegramPage = new TabPage("텔레그램 연결") { BackColor = UiTheme.Surface, AutoScroll = true };
        tabs.TabPages.Add(journeyPage); tabs.TabPages.Add(telegramPage);
        tabs.SelectedIndex = openTelegram || firstRun ? 1 : 0;
        var journeyLayout = FieldLayout(10);
        Span(journeyLayout, SectionHeading("원하는 열차 조건"), 0);
        Span(journeyLayout, Hint("코레일에서 같은 구간·날짜로 직접 조회한 뒤 열차를 고르세요.\n아래 조건은 조회 목록에서 대상을 고를 때 사용합니다."), 1);
        Span(journeyLayout, journeyEnabled, 2);
        Row(journeyLayout, 3, "출발역", departure); Row(journeyLayout, 4, "도착역", arrival); Row(journeyLayout, 5, "승차 날짜", date);
        var times = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        from.Width = 108; to.Width = 108; times.Controls.Add(from);
        times.Controls.Add(new Label { Text = "부터", AutoSize = true, Margin = new Padding(8, 7, 8, 0) }); times.Controls.Add(to);
        times.Controls.Add(new Label { Text = "까지 출발", AutoSize = true, Margin = new Padding(8, 7, 0, 0) });
        Row(journeyLayout, 6, "출발 시간대", times); Row(journeyLayout, 7, "열차번호 목록", train); Row(journeyLayout, 8, "좌석 등급", grade);
        Span(journeyLayout, Hint("열차번호 예: 505,541,561 · 쉼표로 최대 20개, 빈칸도 가능\n입력한 순서대로 확인하며, 선택한 열차 중 한 편만 예약합니다."), 9);
        journeyPage.Controls.Add(journeyLayout);

        var telegramLayout = FieldLayout(9);
        Span(telegramLayout, SectionHeading("예약 결과를 텔레그램으로"), 0);
        Span(telegramLayout, Hint("1. BotFather에서 /newbot으로 본인의 봇을 만드세요.\n2. 만든 봇과 대화를 열고 /start를 보내세요.\n3. 토큰 입력 → Chat ID 찾기 → 테스트 → 알림 체크 후 저장"), 1);
        var botFather = new LinkLabel { Text = "BotFather에서 봇 만들기 ↗", AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, LinkColor = UiTheme.Accent, ActiveLinkColor = UiTheme.Accent, VisitedLinkColor = UiTheme.Accent };
        botFather.LinkClicked += delegate {
            if (working) return;
            try { Process.Start(new ProcessStartInfo("https://t.me/BotFather") { UseShellExecute = true }); }
            catch { SetNotice("브라우저를 열지 못했습니다. 텔레그램에서 공식 @BotFather를 직접 찾아주세요.", true); }
        };
        Span(telegramLayout, botFather, 2); Row(telegramLayout, 3, "내 봇 토큰", token); Row(telegramLayout, 4, "내 Chat ID", chatId);
        var telegramActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        telegramActions.Controls.Add(findChat); telegramActions.Controls.Add(test);
        Span(telegramLayout, telegramActions, 5); Span(telegramLayout, telegramEnabled, 6);
        Span(telegramLayout, Hint("테스트 후에도 ‘설정 저장’을 눌러야 알림 사용이 적용됩니다.\n개인 대화만 찾을 수 있으며, 그룹 ID는 직접 입력하세요.\n토큰은 이 PC의 Windows 계정으로 암호화해 저장합니다.\n예약 완료 확인 알림이며 결제는 직접 진행하세요."), 7);
        Span(telegramLayout, disconnect, 8);
        telegramPage.Controls.Add(telegramLayout);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        var cancel = new Button { Text = firstRun ? "나중에" : "취소", Width = 104, Height = 42, DialogResult = DialogResult.Cancel };
        UiTheme.StyleButton(cancel); actions.Controls.Add(save); actions.Controls.Add(cancel);
        notice.Padding = new Padding(0, 14, 0, 0);
        root.Controls.Add(heading, 0, 0); root.Controls.Add(tabs, 0, 1); root.Controls.Add(notice, 0, 2); root.Controls.Add(actions, 0, 3);
        Controls.Add(root); CancelButton = cancel; AcceptButton = save;
        grade.Items.AddRange(new object[] { "일반실", "특실", "우등실" });
        journeyEnabled.Checked = initial.Journey.Enabled; departure.Text = initial.Journey.DepartureStation; arrival.Text = initial.Journey.ArrivalStation;
        date.Value = Parse(initial.Journey.Date, "yyyy-MM-dd", DateTime.Today);
        from.Value = Parse(initial.Journey.FromTime, "HH:mm", DateTime.Today); to.Value = Parse(initial.Journey.ToTime, "HH:mm", DateTime.Today.AddHours(23).AddMinutes(59));
        train.Text = initial.Journey.TrainNumber; grade.SelectedItem = initial.Journey.Grade;
        if (grade.SelectedIndex < 0) grade.SelectedIndex = 0;
        telegramEnabled.Checked = firstRun || initial.Telegram.Enabled; token.Text = initial.Telegram.BotToken; chatId.Text = initial.Telegram.ChatId;
        notice.Text = firstRun
            ? "먼저 본인의 봇 토큰과 Chat ID를 입력하고 저장하세요. ‘나중에’를 누르면 연결 없이 시작하며, 다음 실행 때 다시 안내합니다."
            : "변경한 두 탭의 설정은 ‘저장’을 눌러야 적용됩니다. 취소하면 기존 설정을 유지합니다.";
        if (firstRun) Shown += delegate { token.Focus(); };
        save.Click += delegate {
            if (working) return;
            var value = ReadFields(); string issue = value.Journey.Validate();
            if (issue != null) { tabs.SelectedTab = journeyPage; SetNotice(issue, true); return; }
            var requiredTelegram = value.Telegram.Copy();
            if (firstRun) requiredTelegram.Enabled = true;
            issue = requiredTelegram.Validate();
            if (issue != null) { tabs.SelectedTab = telegramPage; SetNotice(issue, true); return; }
            Result = value; DialogResult = DialogResult.OK; Close();
        };
        test.Click += async delegate {
            if (working) return;
            TelegramSettings selected = ReadFields().Telegram; selected.Enabled = true;
            string issue = selected.Validate();
            if (issue != null) { SetNotice(issue, true); return; }
            SetWorking(true); SetNotice("테스트 알림을 보내는 중입니다…", false);
            string outcome = await TelegramNotifier.SendAsync(selected, "[코레일 한 장 도우미] 텔레그램 연결 테스트입니다. 예약 완료가 확인되면 이 대화로 알립니다. 결제는 직접 진행하세요.");
            if (IsDisposed) return;
            SetWorking(false);
            notice.ForeColor = outcome == TelegramNotifier.SuccessMessage ? Color.FromArgb(20, 113, 71) : UiTheme.Danger;
            notice.Text = outcome == TelegramNotifier.SuccessMessage ? outcome + " 내 대화에 도착했는지 확인하고 알림 사용을 체크한 뒤 ‘저장’을 누르세요." : outcome;
        };
        findChat.Click += async delegate {
            if (working) return;
            string capturedToken = token.Text.Trim();
            string issue = new TelegramSettings { Enabled = true, BotToken = capturedToken, ChatId = "1" }.Validate();
            if (issue != null) { SetNotice(issue, true); return; }
            SetWorking(true); SetNotice("이 봇의 최근 개인 대화를 조회하는 중입니다…", false);
            TelegramChatLookup found = await TelegramNotifier.FindChatsAsync(capturedToken);
            if (IsDisposed) return;
            SetWorking(false);
            if (found.Error != null) { SetNotice(found.Error, true); return; }
            using (var choices = new TelegramChatDialog(found.Chats))
            {
                if (choices.ShowDialog(this) == DialogResult.OK && choices.SelectedChat != null)
                {
                    chatId.Text = choices.SelectedChat.ChatId; notice.ForeColor = UiTheme.Muted;
                    notice.Text = "선택한 Chat ID를 입력했습니다. 테스트 알림이 내 대화에 도착하는지 확인한 뒤 저장하세요.";
                }
                else notice.Text = "대화 선택을 취소했습니다. Chat ID는 변경하지 않았습니다.";
            }
        };
        disconnect.Click += delegate {
            if (working) return;
            token.Clear(); chatId.Clear(); telegramEnabled.Checked = false;
            SetNotice("이 설정 창의 연결 정보를 비웠습니다. ‘저장’을 누르면 알림이 꺼지고 저장된 토큰이 지워집니다. 취소하면 기존 연결을 유지합니다.", false);
        };
    }
    void SetWorking(bool value)
    {
        working = value;
        // Freeze both tabs so a completed request always refers to the credentials still shown.
        tabs.Enabled = !value; save.Enabled = !value;
    }
    void SetNotice(string message, bool error)
    {
        notice.ForeColor = error ? UiTheme.Danger : UiTheme.Muted; notice.Text = message;
    }
    static TableLayoutPanel FieldLayout(int rowCount)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(20), ColumnCount = 2, RowCount = rowCount, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = UiTheme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < rowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.SizeChanged += delegate {
            int available = Math.Max(200, layout.ClientSize.Width - layout.Padding.Horizontal - 8);
            foreach (Control control in layout.Controls)
            {
                if (!(control is Label) || layout.GetColumnSpan(control) != 2) continue;
                if (control.MaximumSize.Width != available) control.MaximumSize = new Size(available, 0);
            }
        };
        return layout;
    }
    static Label SectionHeading(string text)
    {
        return new Label { Text = text, AutoSize = true, Font = new Font("맑은 고딕", 13, FontStyle.Bold), ForeColor = UiTheme.Text };
    }
    static Label Hint(string text)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = UiTheme.Muted };
    }
    AppSettings ReadFields()
    {
        return new AppSettings {
            Journey = new JourneySettings { Enabled = journeyEnabled.Checked, DepartureStation = departure.Text.Trim(), ArrivalStation = arrival.Text.Trim(), Date = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FromTime = from.Value.ToString("HH:mm", CultureInfo.InvariantCulture), ToTime = to.Value.ToString("HH:mm", CultureInfo.InvariantCulture), TrainNumber = train.Text.Trim(), Grade = (string)grade.SelectedItem },
            Telegram = new TelegramSettings { Enabled = telegramEnabled.Checked, BotToken = token.Text.Trim(), ChatId = chatId.Text.Trim() }
        };
    }
    static DateTime Parse(string value, string format, DateTime fallback)
    {
        DateTime parsed;
        return DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) && parsed >= new DateTime(1753, 1, 1) && parsed <= new DateTime(9998, 12, 31) ? parsed : fallback;
    }
    static void Span(TableLayoutPanel layout, Control control, int row)
    {
        control.Margin = new Padding(0, 0, 0, 16);
        if (control is Label) { control.AutoSize = true; control.Anchor = AnchorStyles.Left | AnchorStyles.Top; control.MaximumSize = new Size(600, 0); }
        layout.Controls.Add(control, 0, row); layout.SetColumnSpan(control, 2);
    }
    static void Row(TableLayoutPanel layout, int row, string caption, Control input)
    {
        layout.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = UiTheme.Text, Margin = new Padding(0, 6, 12, 16) }, 0, row);
        input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 0, 0, 14); layout.Controls.Add(input, 1, row);
    }
}
