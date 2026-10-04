using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;
public sealed partial class RuntimeHost
{
    private sealed record MemorySyncConnection(Func<RgbColor,Task> Write,Func<bool,Task> End);
    private async Task<MemorySyncConnection> OpenMemorySyncAsync(string[] identities)
    {
        var targets = identities.Select(id => new MemoryRgbTarget(id,
            Rgb.Devices.Single(d => d.ControlIdentity == id).Zones.Single().Configuration ?? throw new IOException("メモリの現在設定を再取得してください"))).ToArray();
        if (Coordinator is RemoteControlCoordinator remote)
        {
            var lease = await remote.BeginMemorySyncAsync(targets);
            return new(c => remote.WriteMemorySyncAsync(lease,c),keep => remote.EndMemorySyncAsync(lease,keep));
        }
        var direct = new EneMemoryRgbControl(recoveryDirectory: Store.DirectoryPath);
        var localLease = await direct.BeginAsync(targets);
        return new(c => direct.FrameAsync(localLease,c),keep => direct.EndAsync(localLease,keep));
    }
}
