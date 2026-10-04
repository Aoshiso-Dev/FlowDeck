using System.Buffers.Binary;
using System.Text;
using FlowDeck.Infrastructure;
using static FlowDeck.Tests.OpenRgbSdkTestServer;

namespace FlowDeck.Tests;

public sealed partial class OpenRgbSdkEnvironmentTests
{
    [Theory]
    [InlineData("Client")] [InlineData("ProfileManager")]
    [InlineData("QMKOpenRGBDevices")] [InlineData("QMKVialRGBDevices")]
    [InlineData("ASRock Polychrome USB")]
    public async Task StartupCategoriesAreReadTwiceAndChangedObservationsInvalidateTheReceipt(string changed)
    {
        await using var r = new Rig(); var reads = 0; var mutate = false;
        r.Before = async packet =>
        {
            if (packet.Command == 52 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Data) == 17)
            { await r.Server.ReplyAsync(packet, 53, Numbers(r.Server.Flags)); return true; }
            if (packet.Command == 250 && Encoding.UTF8.GetString(packet.Data) == changed + '\0' && mutate && ++reads == 2)
                r.StartupSettings[changed] = "{\"changed\":true}";
            return false;
        };
        await using var connection = await OpenRgbSdkConnection.ConnectAsync(r.Server.Port, ownedHeadless: true,
            verifyPeer: (client, remote) => WindowsTcpPeer.Check((uint)Environment.ProcessId, client, remote));
        var receipt = await connection.ReadEnvironmentAsync(); Assert.NotNull(receipt.Startup);
        Assert.Equal(18, r.Server.Requests.Count(p => p.Command is 250 or 251));
        mutate = true;
        await Assert.ThrowsAsync<OpenRgbSdkObservationChangedException>(() => connection.ReadEnvironmentAsync());
        Assert.Throws<IOException>(() => connection.CheckEnvironment(receipt.Stamp));
        Assert.DoesNotContain(r.Server.Requests, p => p.Command is 200 or 1050 or 1051 or 1101 or 1103);
    }
}
