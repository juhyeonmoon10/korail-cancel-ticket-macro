using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

// Stores only bounded, allowlisted facts. Never retain a URL, page message,
// request/response body, header, cookie, account identifier or credential.
internal sealed class SiteDiagnostics
{
    const int MaxEvents = 100;
    readonly object sync = new object();
    readonly Queue<DiagnosticEvent> events = new Queue<DiagnosticEvent>();
    string runtimeVersion = "", sdkVersion = "", blockCode = "";
    bool manualMode, siteBlocked;

    static readonly HashSet<string> Actions = new HashSet<string>(StringComparer.Ordinal) {
        "start", "browser-start", "manual-mode", "helper-mode", "mode-manual", "mode-helper",
        "native-query", "native-more", "native-seat", "native-notice", "native-reserve", "native-receipt-notice", "native-receipt-scroll", "completion-check", "helper-initialized", "diagnostic-export",
        "page-query", "page-more", "page-seat", "page-notice", "page-reserve", "page-receipt-notice", "page-receipt-scroll", "page-stop-notice"
    };
    static readonly HashSet<string> NavigationStatuses = new HashSet<string>(StringComparer.Ordinal) {
        "Unknown", "CertificateCommonNameIsIncorrect", "CertificateExpired",
        "ClientCertificateContainsErrors", "CertificateRevoked", "CertificateIsInvalid",
        "ServerUnreachable", "Timeout", "ErrorHttpInvalidServerResponse", "ConnectionAborted",
        "ConnectionReset", "Disconnected", "CannotConnect", "HostNameNotResolved",
        "OperationCanceled", "RedirectFailed", "UnexpectedError",
        "ValidAuthenticationCredentialsRequired", "ValidProxyAuthenticationRequired"
    };

    public string WebViewRuntime
    {
        get { lock (sync) return runtimeVersion; }
        set { lock (sync) runtimeVersion = SafeVersion(value); }
    }
    public string SdkVersion
    {
        get { lock (sync) return sdkVersion; }
        set { lock (sync) sdkVersion = SafeVersion(value); }
    }
    public bool ManualMode
    {
        get { lock (sync) return manualMode; }
        set { lock (sync) manualMode = value; }
    }
    public bool SiteBlocked { get { lock (sync) return siteBlocked; } }
    public string BlockCode { get { lock (sync) return blockCode; } }

    public bool TryBlock(string messageOrCode)
    {
        if (String.IsNullOrEmpty(messageOrCode) || messageOrCode.Length > 8192) return false;
        string value = messageOrCode.Trim();
        bool exactKnownCode = Regex.IsMatch(value, @"^(?:CODE\s*:\s*)?-8002$", RegexOptions.IgnoreCase);
        bool explicitMessage = Regex.IsMatch(value, @"자동화된\s*요청으로\s*감지되어\s*차단되었습니다");
        if (!exactKnownCode && !explicitMessage) return false;
        string code = exactKnownCode ? "-8002" : "";
        if (explicitMessage)
        {
            Match match = Regex.Match(value, @"\bCODE\s*:\s*(-[0-9]{1,6})(?![0-9])", RegexOptions.IgnoreCase);
            if (match.Success) code = match.Groups[1].Value;
        }
        lock (sync)
        {
            bool changed = !siteBlocked || (blockCode.Length == 0 && code.Length != 0);
            siteBlocked = true;
            if (blockCode.Length == 0) blockCode = code;
            if (changed) AddEvent(new DiagnosticEvent("site-block", blockCode, false, ""));
        }
        return true;
    }

    public void AddNavigation(string url, bool success, string status)
    {
        string category = NavigationCategory(url), safeStatus = SafeNavigationStatus(status);
        lock (sync) AddEvent(new DiagnosticEvent("navigation", category, success, safeStatus));
    }

    public void AddAction(string kind)
    {
        if (kind == null || !Actions.Contains(kind)) return;
        lock (sync) AddEvent(new DiagnosticEvent("action", kind, false, ""));
    }

    public object Snapshot()
    {
        lock (sync)
        {
            var copied = new List<Dictionary<string, object>>();
            foreach (DiagnosticEvent item in events)
            {
                var row = new Dictionary<string, object> {
                    { "atUtc", item.AtUtc }, { "kind", item.Kind }
                };
                if (item.Kind == "navigation")
                {
                    row.Add("category", item.Value); row.Add("success", item.Success); row.Add("status", item.Status);
                }
                else if (item.Kind == "site-block") row.Add("blockCode", item.Value);
                else row.Add("action", item.Value);
                copied.Add(row);
            }
            return new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "webViewRuntime", runtimeVersion }, { "sdkVersion", sdkVersion },
                { "manualMode", manualMode }, { "siteBlocked", siteBlocked }, { "blockCode", blockCode },
                { "events", copied.ToArray() }
            };
        }
    }

    static string SafeVersion(string value)
    {
        if (String.IsNullOrEmpty(value) || value.Length > 64) return "";
        return Regex.IsMatch(value, @"\A[0-9]+(?:\.[0-9]+){0,5}\z") ? value : "";
    }

    static string SafeNavigationStatus(string value)
    {
        if (value != null && NavigationStatuses.Contains(value)) return value;
        if (value != null && Regex.IsMatch(value, @"\A[1-5][0-9]{2}\z")) return value;
        return "unrecognized";
    }

    static string NavigationCategory(string value)
    {
        Uri uri;
        if (String.IsNullOrEmpty(value) || value.Length > 8192 ||
            !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || !uri.IsDefaultPort)
            return "other";
        if ((uri.Host == "www.korail.com" || uri.Host == "korail.com") &&
            uri.AbsolutePath.StartsWith("/ticket/", StringComparison.Ordinal)) return "korail-ticket";
        if (uri.Host == "korail-helper.invalid" &&
            (uri.AbsolutePath == "/demo.html" || uri.AbsolutePath == "/card-demo.html")) return "local-demo";
        return "other";
    }

    void AddEvent(DiagnosticEvent item)
    {
        if (events.Count == MaxEvents) events.Dequeue();
        events.Enqueue(item);
    }

    sealed class DiagnosticEvent
    {
        public readonly string AtUtc, Kind, Value, Status;
        public readonly bool Success;
        public DiagnosticEvent(string kind, string value, bool success, string status)
        {
            AtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Kind = kind; Value = value; Success = success; Status = status;
        }
    }
}
