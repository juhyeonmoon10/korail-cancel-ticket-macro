using System;

// A matching form alone is not proof that new results were fetched. Require a
// navigation or a result-list mutation after the native submit, then stability.
internal sealed class QueueQueryProgress
{
    bool submitted;
    int page, stablePage;
    string source, stableFingerprint, stableSource;
    double revision, stableRevision;
    DateTime stableSince;
    public void Reset() { submitted = false; stableFingerprint = null; }
    public void Submit(int navigation, string url, double resultRevision)
    {
        submitted = true; page = navigation; source = url; revision = resultRevision; stableFingerprint = null;
    }
    public bool Ready(int navigation, string url, double resultRevision, string fingerprint, DateTime now)
    {
        if (!submitted || String.IsNullOrEmpty(fingerprint) || (page == navigation && source == url && revision == resultRevision)) { stableFingerprint = null; return false; }
        // Identical text can come from replaced rows or a second navigation.
        // Every fresh document/result observation needs its own stable period.
        if (stableFingerprint != fingerprint || stablePage != navigation || stableSource != url || stableRevision != resultRevision)
        {
            stableFingerprint = fingerprint; stablePage = navigation; stableSource = url; stableRevision = resultRevision;
            stableSince = now; return false;
        }
        return (now - stableSince).TotalMilliseconds >= 750;
    }
    public void Unstable() { stableFingerprint = null; }
}
