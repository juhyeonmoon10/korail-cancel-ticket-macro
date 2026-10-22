using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

internal sealed partial class MainWindow
{
    SavedTrainSelection savedSelection;
    string SelectionFile { get { return Path.Combine(Program.DataRoot, "selected-trains.json"); } }

    void LoadSelectionMemory()
    {
        try { savedSelection = SavedTrainSelection.Load(SelectionFile); }
        catch { savedSelection = null; status.Text = "저장한 열차를 읽지 못했습니다. 조회 화면에서 다시 선택하세요."; }
    }

    void ReceiveSelection(Dictionary<string, object> message)
    {
        if (Program.SelfTest || Program.ManualDiagnostic || !OfficialTicketSource(view.CoreWebView2.Source) || queueRunning) return;
        var next = SavedTrainSelection.FromMessage(ObjectValue(message, "selection"), ObjectValue(message, "query"));
        next.Save(SelectionFile); savedSelection = next;
    }

    async Task RestoreSavedSelection()
    {
        if (!ready || closing || Program.ManualDiagnostic || savedSelection == null) return;
        await view.CoreWebView2.ExecuteScriptAsync("window.KorailDesktopHelper && window.KorailDesktopHelper.restoreSelection && window.KorailDesktopHelper.restoreSelection(" + json.Serialize(savedSelection.ForPage()) + ")");
    }

    async Task<SavedTrainSelection> ReadSelectionForQueue()
    {
        if (!ready || closing || !AutomationAvailable()) throw new InvalidOperationException("코레일 조회 화면을 먼저 여세요.");
        if (!OfficialTicketSource(view.CoreWebView2.Source)) throw new InvalidOperationException("실제 코레일 조회 결과에서 원하는 열차를 적용하세요.");
        int page = navigation; string source = view.CoreWebView2.Source;
        if (navigationPending) throw new InvalidOperationException("조회 화면 로딩이 끝난 뒤 추가하세요.");
        string raw = await view.CoreWebView2.ExecuteScriptAsync("(() => {const h=window.KorailDesktopHelper,s=window.KorailSiteAdapter;return h&&s?{selection:h.snapshot(),query:s.query(document)}:null;})()");
        if (closing || MouseDocumentChanged(page, source)) throw new InvalidOperationException("화면이 바뀌었습니다. 조회 결과에서 다시 추가하세요.");
        var data = json.Deserialize<Dictionary<string, object>>(raw);
        var selection = SavedTrainSelection.FromMessage(ObjectValue(data, "selection"), ObjectValue(data, "query"));
        if (!selection.Confirmed) throw new InvalidOperationException("선택한 열차의 날짜·구간·등급 확인란을 체크하세요.");
        return selection;
    }
}
