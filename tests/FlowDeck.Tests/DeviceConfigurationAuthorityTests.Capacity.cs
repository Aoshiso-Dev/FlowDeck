using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceConfigurationAuthorityTests
{
    [Theory] [InlineData(65, 0, 1, "64")] [InlineData(0, 129, 1, "128")] [InlineData(0, 2, 2049, "4096")]
    public async Task ServiceRejectsRawOverCapacityUploadsBeforeReleaseAndRetainsActiveGeneration(int fans, int lights, int leds, string limit)
    {
        await using var rig = await Rig.Create();
        Assert.True((await rig.Startup.Runtime.ExecuteAsync(rig.Duty(62))).Success);
        var generation = rig.Startup.Generation.Id; var writes = rig.Hub.Writes;
        // Bypass the client codec to verify the service trust boundary independently.
        var package = DeviceControlCapacityTests.Package(fans, lights, leds);
        package = package with { Bindings = package.Bindings with { InstallationId = rig.Startup.Configuration.Bindings.InstallationId } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(package, Contract.Json);
        var head = await rig.Send(new(DeviceConfigurationOperation.BeginUpload, ExpectedGeneration: generation,
            TotalBytes: bytes.Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))));
        Assert.True(head.Success, head.Error); var id = head.Configuration!.TransferId;
        for (var offset = 0; offset < bytes.Length; offset += DeviceConfigurationProtocol.ChunkBytes)
            Assert.True((await rig.Send(new(DeviceConfigurationOperation.Append, id, Offset: offset,
                Data: bytes.AsSpan(offset, Math.Min(DeviceConfigurationProtocol.ChunkBytes, bytes.Length - offset)).ToArray()))).Success);
        var rejected = await rig.Send(new(DeviceConfigurationOperation.Validate, id));
        Assert.False(rejected.Success); Assert.Contains(limit, rejected.Error);
        Assert.False((await rig.Send(rig.Apply(id!))).Success);
        Assert.Equal(generation, rig.Startup.Generation.Id); Assert.Equal(writes, rig.Hub.Writes);
        Assert.Equal(62, rig.Startup.Runtime.State().Endpoints.Single(e => e.Target.EndpointId == "fan1").RequestedDuty);
        Assert.True((await rig.Renew()).Success);
    }
}
