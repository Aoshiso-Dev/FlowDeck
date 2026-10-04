using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed class DeviceServiceStateValidationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MalformedRequestedAndReportedLightingCannotEnterEitherStateReader(bool reported, bool chunked)
    {
        LightingProgram[] invalid = [
            new("fixed", null!), new("fixed", [null!]), new("fixed", [new(-1, 20, 30)]),
            new("fixed", [new(10, 256, 30)]), new("fixed", [new(10, 20, 30)], Brightness: 101),
            new("fixed", [new(10, 20, 30)], Brightness: -1), new("fixed", [new(10, 20, 30)], Speed: 256)
        ];
        var id = new string('a', 32); var hello = new DeviceServiceHello(2, id, id, 12, []);
        DeviceServiceState State(LightingProgram program) => new(new(id, 1, []),
            [new(new("demo", "led1"), 1, 1, Outcome.WrittenUnverified,
                RequestedLighting: reported ? null : program, ReportedLighting: reported ? program : null)], false, null);
        DeviceServiceReply Read(LightingProgram program)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new DeviceServiceReply(2, id, id, id, true, State: State(program)), Contract.Json);
            return chunked ? DeviceServiceProtocol.ReadStateSnapshot(bytes, hello) : DeviceServiceProtocol.ReadReply(bytes, hello);
        }
        foreach (var program in invalid) Assert.Throws<IOException>(() => Read(program));
        Assert.NotNull(Read(new("fixed", [new(0, 255, 128)], Brightness: 0)).State);
        Assert.NotNull(Read(new("breathing", [new(255, 0, 128)], Speed: 4, Brightness: 100)).State);
        Assert.NotNull(Read(new("device-specific", [new(1, 2, 3)], Speed: 255)).State);
    }
}
