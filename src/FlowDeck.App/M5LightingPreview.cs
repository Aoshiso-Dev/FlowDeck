using System.Text.Json;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    private static readonly string[] VirtualLightMenu = ["対象グループ", "シーン", "明るさ", "消灯", "元に戻す", "同期停止", "NZXTプリセット"];
    private JsonElement virtualLightState, virtualLightCatalog;
    private string virtualLightScope = "all", virtualLightStamp = "", virtualCatalogRequest = "";
    private int virtualLightMenuIndex, virtualCatalogIndex, virtualCatalogTotal, virtualCatalogPage, virtualCatalogSequence, virtualBrightness;
    private bool virtualLightingResult;
    private string VirtualScopeName => virtualLightState.ValueKind == JsonValueKind.Object ? virtualLightState.GetProperty("scopeName").GetString()! : "取得中";
    private string VirtualLightingDisplay
    {
        get
        {
            var hint = virtualLightState.ValueKind == JsonValueKind.Object && virtualLightState.GetProperty("stamp").GetString() != virtualLightStamp ? "設定変更あり・開き直してください" : "B長押しで破棄・ホーム";
            if (virtualPage == "lightBrightness") return $"照明・明るさ\n{VirtualScopeName}\n\n{virtualBrightness}%  設定値\n{hint}\nA −5    B 適用    C ＋5";
            var menu = virtualPage == "lightMenu"; var selected = menu ? virtualLightMenuIndex : virtualCatalogIndex;
            var labels = menu ? VirtualLightMenu : virtualLightCatalog.ValueKind == JsonValueKind.Object
                ? virtualLightCatalog.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray() : [];
            var start = menu ? selected / 4 * 4 : selected % 8 / 4 * 4;
            var lines = string.Join("\n", labels.Skip(start).Take(4).Select((label, i) => (start + i == (menu ? selected : selected % 8) ? "▶ " : "   ") + label));
            return $"照明・{(menu ? "操作" : virtualPage == "lightGroups" ? "対象グループ" : "シーン")} / {VirtualScopeName}\n{(labels.Length == 0 ? "一覧なし" : lines)}\n{hint}\nA 前へ    B 決定    C 次へ";
        }
    }
    private void ReceiveVirtualLighting(JsonElement r)
    {
        if (r.GetProperty("sessionId").GetString() != virtualSession || r.GetProperty("scopeId").GetString() != virtualLightScope) return;
        if (r.GetProperty("type").GetString() == "lightingState")
        {
            if (virtualLightState.ValueKind == JsonValueKind.Object && r.GetProperty("lightingSequence").GetInt64() <= virtualLightState.GetProperty("lightingSequence").GetInt64()) return;
            virtualLightState = r.Clone();
            if (virtualPage == "lightMenu" && virtualLightStamp.Length == 0 && !r.GetProperty("busy").GetBoolean()) virtualLightStamp = r.GetProperty("stamp").GetString()!;
        }
        else if (virtualPage is "lightGroups" or "lightScenes" && r.GetProperty("requestId").GetString() == virtualCatalogRequest)
        {
            virtualLightCatalog = r.Clone(); virtualCatalogTotal = r.GetProperty("total").GetInt32();
            if (virtualCatalogIndex >= virtualCatalogTotal && virtualCatalogTotal > 0) { virtualCatalogIndex = 0; RequestVirtualCatalog(); return; }
            virtualLightStamp = r.GetProperty("stamp").GetString()!;
        }
    }
    private void OpenVirtualLights()
    {
        virtualPage = "lightMenu"; virtualLightMenuIndex = 0; virtualLightStamp = "";
        SendVirtual("getLightingState", new() { ["scopeId"] = virtualLightScope });
    }
    private void RequestVirtualCatalog()
    {
        virtualCatalogPage = virtualCatalogIndex / 8; virtualLightCatalog = default; virtualCatalogRequest = (++virtualCatalogSequence).ToString();
        SendVirtual("getLightingCatalog", new() { ["scopeId"] = virtualLightScope, ["kind"] = virtualPage == "lightGroups" ? "groups" : "scenes", ["page"] = virtualCatalogPage, ["requestId"] = virtualCatalogRequest });
    }
    private void SubmitVirtualLighting(string command, string? sceneId = null)
    {
        virtualLightingResult = true;
        if (virtualLightState.ValueKind != JsonValueKind.Object || !virtualLightState.GetProperty("ready").GetBoolean() ||
            virtualLightState.GetProperty("stamp").GetString() != virtualLightStamp)
        { virtualMessage = "設定が変更されました。照明を開き直してください"; virtualPage = "result"; return; }
        var payload = new Dictionary<string, object?> { ["scopeId"] = virtualLightScope, ["expectedStamp"] = virtualLightStamp };
        if (sceneId is not null) payload["sceneId"] = sceneId;
        if (command == "SetLightingBrightness") payload["brightness"] = virtualBrightness;
        virtualPage = "pending";
        SendVirtual("command", new() { ["id"] = "virtual-" + ++virtualSequence, ["commandSequence"] = virtualSequence, ["expectedRevision"] = 0, ["command"] = command, ["payload"] = payload });
    }
    private bool VirtualLightingPress(string button)
    {
        if (virtualPage == "home" && button == "C" || virtualPage == "result" && virtualLightingResult && button == "B") { OpenVirtualLights(); return true; }
        if (virtualPage is not ("lightMenu" or "lightGroups" or "lightScenes" or "lightBrightness")) return false;
        if (button != "B")
        {
            var delta = button == "A" ? -1 : 1;
            if (virtualPage == "lightMenu") virtualLightMenuIndex = (virtualLightMenuIndex + delta + 7) % 7;
            else if (virtualPage == "lightBrightness") virtualBrightness = Math.Clamp(virtualBrightness + delta * 5, 1, 100);
            else if (virtualCatalogTotal > 0 && virtualLightCatalog.ValueKind == JsonValueKind.Object)
            { virtualCatalogIndex = (virtualCatalogIndex + delta + virtualCatalogTotal) % virtualCatalogTotal; if (virtualCatalogIndex / 8 != virtualCatalogPage) RequestVirtualCatalog(); }
        }
        else if (virtualPage == "lightMenu")
        {
            if (virtualLightMenuIndex is 0 or 1) { virtualPage = virtualLightMenuIndex == 0 ? "lightGroups" : "lightScenes"; virtualCatalogIndex = 0; RequestVirtualCatalog(); }
            else if (virtualLightMenuIndex == 2 && virtualLightState.ValueKind == JsonValueKind.Object) { virtualBrightness = virtualLightState.GetProperty("brightness").GetInt32(); virtualPage = "lightBrightness"; }
            else if (virtualLightMenuIndex == 3) SubmitVirtualLighting("LightingOff");
            else if (virtualLightMenuIndex == 4 && virtualLightState.GetProperty("canRestore").GetBoolean()) SubmitVirtualLighting("RestoreLighting");
            else if (virtualLightMenuIndex == 5 && virtualLightState.GetProperty("synchronized").GetBoolean()) SubmitVirtualLighting("StopLightingSync");
            else if (virtualLightMenuIndex == 6 && state.HubReady) { virtualPage = "lighting"; virtualSelection = 0; virtualEditRevision = state.LightingRevision; }
        }
        else if (virtualPage == "lightBrightness") SubmitVirtualLighting("SetLightingBrightness");
        else if (virtualCatalogTotal > 0 && virtualLightCatalog.ValueKind == JsonValueKind.Object)
        {
            var id = virtualLightCatalog.GetProperty("items")[virtualCatalogIndex % 8].GetProperty("id").GetString()!;
            if (virtualPage == "lightGroups") { virtualLightScope = id; OpenVirtualLights(); }
            else SubmitVirtualLighting("ApplyLightingScene", id);
        }
        return true;
    }
}
