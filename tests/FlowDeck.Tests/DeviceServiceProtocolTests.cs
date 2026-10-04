using System.Text;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DeviceServiceProtocolTests
{
    [Fact]
    public async Task FramedRoundTripRetainsGenericStateAndRejectsAnotherServiceEpoch()
    {
        var driver = new DemoEndpointDriver("demo", "模擬機器", DeviceKind.Controller); await using var runtime = new DeviceControlRuntime([driver], [ServiceFixture.Sid], new TestLog(), demo: true);
        var hello = runtime.OpenSession(ServiceFixture.Sid); using var stream = new MemoryStream();
        await CoolingPipeFrames.WriteAsync(stream, hello, default); stream.Position = 0;
        var decoded = DeviceServiceProtocol.ReadHello(await CoolingPipeFrames.ReadAsync(stream, default)); Assert.True(decoded.Demo);
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.State);
        var reply = await runtime.ExecuteAsync(request); var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, Contract.Json);
        Assert.Equal("demo", Assert.Single(DeviceServiceProtocol.ReadReply(bytes, hello).State!.Inventory.Devices).Device.Id);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(bytes, hello with { Epoch = Guid.NewGuid().ToString("N") }));
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadReply(bytes, hello with { Session = Guid.NewGuid().ToString("N") }));
    }
    [Fact]
    public void DuplicateHandshakeFieldsAndUnboundedFramesAreRejected()
    {
        var hello = new DeviceServiceHello(2, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 12, [], true);
        var json = JsonSerializer.Serialize(hello, Contract.Json);
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadHello(Encoding.UTF8.GetBytes("{\"Version\":2," + json[1..])));
        Assert.Throws<IOException>(() => DeviceServiceProtocol.ReadHello(new byte[DeviceServiceProtocol.MaximumFrameBytes + 1]));
        Assert.Throws<ArgumentException>(() => DeviceServicePipeClient.ForDemo(DeviceServiceProtocol.PipeName, Environment.ProcessId));
    }
    [Fact]
    public async Task MotherboardAndMemoryLightingDoNotDependOnAHubDriver()
    {
        var board = new DemoEndpointDriver("board", "マザー", DeviceKind.Motherboard, cooling: false);
        var memory = new DemoEndpointDriver("memory", "メモリ", DeviceKind.Memory, cooling: false);
        await using var runtime = new DeviceControlRuntime([board, memory], [ServiceFixture.Sid], new TestLog(), demo: true);
        var hello = runtime.OpenSession(ServiceFixture.Sid); var state = runtime.State();
        var request = new DeviceServiceRequest(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.SetLighting,
            state.Inventory.TopologyRevision, state.Endpoints.Select(s => new DeviceTargetVersion(s.Target, s.Generation, s.PolicyRevision)).ToArray(), Lighting: new("fixed", [new(9, 99, 199)]));
        var result = await runtime.ExecuteAsync(request); Assert.Equal(Outcome.WrittenUnverified, result.Result!.Outcome);
        Assert.Equal(board.Color.Colors, memory.Color.Colors); Assert.Equal(1, board.LightWrites); Assert.Equal(1, memory.LightWrites);
        Assert.DoesNotContain(state.Inventory.Devices.SelectMany(d => d.Device.Endpoints), e => e.Capabilities.Any(c => c.Feature == DeviceFeature.Temperature));
    }
}
