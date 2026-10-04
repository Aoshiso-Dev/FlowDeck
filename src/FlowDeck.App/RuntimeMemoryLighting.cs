using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost
{
    public string? MemoryRgbError { get; private set; }
    private static string NativeLightLabel(string identity, byte zone) => MemoryLighting.IsIdentity(identity) ? "メモリ " + (MemoryLighting.Address(identity) == 0x71 ? "1" : "2") : "マザー / " + zone;
    private async Task<RgbInventory> ReadNativeRgbAsync()
    {
        RgbInventory board;
        try { board = Coordinator.Settings.Motherboard.OpenRgbEnabled ? await new OpenRgbMonitor().ReadAsync(Coordinator.Settings.Motherboard.OpenRgbPort) : await new AsrockRgbMonitor().ReadAsync(); }
        catch (Exception e) { board = new([], Error: e.Message); }
        RgbInventory memory;
        try
        {
            memory = Coordinator is RemoteControlCoordinator remote ? await remote.ReadMemoryRgbAsync() : await new EneMemoryRgbControl(recoveryDirectory: Store.DirectoryPath).ReadAsync();
            MemoryRgbError = memory.Error ?? (memory.Devices.Length == 0 ? "対応するメモリRGBが見つかりません" : null);
        }
        catch (Exception e) { MemoryRgbError = e.Message; memory = new([], Error: e.Message); }
        var devices = board.Devices.Concat(memory.Devices).ToArray();
        return new(devices, DateTimeOffset.UtcNow, devices.Length == 0 ? board.Error ?? memory.Error : null);
    }
    public async Task<SceneApplyRow[]> ApplyMemoryLightingAsync(string[] identities, BoardLightSetting draft, LightingGroup? group)
    {
        if (identities.Length is < 1 or > 2 || identities.Distinct().Count() != identities.Length || identities.Any(id => !MemoryLighting.IsIdentity(id)))
            throw new ArgumentException("メモリを選択してください");
        var desired = MemoryLighting.Normalize(draft);
        using var edit = await LockRgbEditAsync(() =>
        {
            group = ResolveLightingGroup(group); CheckGroupSyncScope(group);
            if (LightingSynchronized) throw new IOException("同期停止後にメモリ照明を変更してください");
            if (group is not null && identities.Any(id => !group.Board.Contains(new(id, 0)))) throw new IOException("グループ外のメモリは変更できません");
        });
        var captured = identities.Select(id => (Id: id, Device: Rgb.Devices.SingleOrDefault(d => d.ControlIdentity == id))).ToArray();
        if (captured.Any(t => t.Device?.Zones.SingleOrDefault()?.Configuration is null)) throw new IOException("メモリの元設定を再取得してください");
        await InvalidateAdjustmentBaselineAsync(new(Guid.NewGuid().ToString("N"), "メモリ照明", [], identities.Select(id => new SceneBoardLight(id, 0, desired)).ToArray()));
        var rows = new List<SceneApplyRow>();
        foreach (var item in captured)
        {
            var result = await ApplyBoardLightingCoreAsync(item.Id, [new(0, item.Device!.Zones[0].Configuration!, desired)]);
            rows.Add(new(item.Device.Name, result.Verified ? "設定一致" : string.Join(" / ", result.Zones.Select(z => z.Error ?? z.Status))));
            if (!result.Verified) break;
        }
        rows.AddRange(captured.Skip(rows.Count).Select(t => new SceneApplyRow(t.Device!.Name, "未送信")));
        return rows.ToArray();
    }
    internal void AddMemoryPreview()
    {
        if (!Demo) throw new InvalidOperationException("メモリプレビューはデモ専用です");
        Rgb = Rgb with { Devices = Rgb.Devices.Where(d => !d.IsMemory).Concat(new byte[] { 0x71, 0x73 }.Select((a, i) =>
            new RgbDevice((uint)(i + 1), "メモリ " + (i + 1), "G.SKILL / ENE", "8 LED", "AUDA0-E6K5-0101", $"AMD SMBus 0 / 0x{a:X2}", [], -1,
                [new("メモリ " + (i + 1), 1, 8, 8, 8, "レインボー", 0, new(14, 255, 255, 255, 0))], MemoryLighting.Identity(a)))).ToArray() };
        MemoryRgbError = null;
    }
}
