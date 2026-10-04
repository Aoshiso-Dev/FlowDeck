using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public partial class DeviceEndpointSetupTests
{
    [Theory] [InlineData("fixed")] [InlineData("breathing")]
    public async Task LightingStartDiagnosticsSeparateSuccessfulOutputFromFailedPersistence(string effect)
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var targets = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        var fan = new EndpointReference(demo.DeviceIds[0], "fan1"); await Duty(client, fan, 62);
        var pointer = Path.Combine(dir.Path, "configuration", "current.json");
        var bytes = File.ReadAllBytes(pointer); var generation = client.Snapshot!.Configuration.Generation;
        // Reading the old configuration remains possible, but Windows denies the atomic replacement.
        // The lock belongs only to this disposable demo, not the user's service configuration.
        using (var hold = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() => LightProgram(client, targets, effect));
            using var trace = System.Text.Json.JsonDocument.Parse(failure.Message);
            var result = System.Text.Json.JsonSerializer.Deserialize<DeviceOperationResult>(trace.RootElement.GetProperty("result"), Contract.Json)!;
            Assert.Equal(Outcome.Partial, result.Outcome);
            Assert.All(result.Targets, t => {
                Assert.Equal(Outcome.WrittenUnverified, t.Outcome);
                Assert.Equal(DevicePolicySaveState.Failed, t.Persistence!.State);
                Assert.Contains("保存できませんでした", t.Persistence.Message);
            });
            Assert.Equal(generation, client.Snapshot!.Configuration.Generation);
            Assert.Equal(bytes, File.ReadAllBytes(pointer));
            Assert.All(client.Snapshot.State.Endpoints.Where(e => targets.Contains(e.Target)), e => {
                Assert.True(e.Owned);
                if (effect == "breathing") Assert.True(e.Lighting!.Running);
            });
            Assert.Equal(62, client.Snapshot.State.Endpoints.Single(e => e.Target == fan).ReportedDuty);
        }
        Assert.True((await client.ReleaseAsync()).Success);
        Assert.All(demo.DeviceIds, id => Assert.Empty(new HubLightingRecoveryJournal(dir.Path, id).Pending));
    }

    [Theory]
    [InlineData("fixed", false)]
    [InlineData("breathing", false)]
    [InlineData("breathing", true)]
    public async Task FailedLightingStartsExposeTargetDetailsAndPreserveUnselectedFanAndLight(string effect, bool expire)
    {
        using var dir = new ServiceTestDirectory();
        await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path, lightingPorts: 3);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var device = demo.DeviceIds[0]; await ConfigureHubLights(client, device);
        var unselected = new EndpointReference(device, "led2");
        var fan = new EndpointReference(device, "fan1");
        await Duty(client, fan, 62); await LightProgram(client, [unselected], "fixed");
        var before = client.Snapshot!.State.Endpoints.Single(e => e.Target == unselected);
        var fanWrites = demo.Hubs.Select(h => h.Writes).ToArray();
        var targets = demo.DeviceIds.Select(id => new EndpointReference(id, "led1")).ToArray();
        if (expire) demo.Hubs[0].DelayMs = 650; // Over the unchanged 500 ms frame deadline, memory-only adapter.
        else demo.Hubs[0].FailNextLightingWrite = true;
        try
        {
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() => LightProgram(client, targets, effect));
            Assert.Contains("lighting-start", failure.Message);
            Assert.Contains(effect, failure.Message);
            Assert.Contains(targets[0].DeviceId, failure.Message);
            Assert.Contains(targets[1].DeviceId, failure.Message);
            if (expire)
                Assert.True(failure.Message.Contains(HardwareWriteDeadline.LightingExpiredError) || failure.Message.Contains("照明フレームの有効期限を超えました"), failure.Message);
            else Assert.Contains("照明応答不明", failure.Message);
            Assert.Contains("persistence", failure.Message);
            var after = client.Snapshot!.State.Endpoints.Single(e => e.Target == unselected);
            Assert.True(after.Owned); Assert.Equal(before.PolicyRevision, after.PolicyRevision);
            Assert.True(DeviceLighting.SameProgram(before.ReportedLighting!, after.ReportedLighting!));
            Assert.Equal(fanWrites, demo.Hubs.Select(h => h.Writes));
            Assert.True(client.Snapshot.State.Endpoints.Single(e => e.Target == fan).Owned);
            Assert.Equal(62, client.Snapshot.State.Endpoints.Single(e => e.Target == fan).ReportedDuty);
            if (effect == "breathing")
                Assert.All(client.Snapshot.State.Endpoints.Where(e => targets.Contains(e.Target)), e => Assert.False(e.Lighting?.Running == true));
        }
        finally { demo.Hubs[0].DelayMs = 0; demo.Hubs[0].FailNextLightingWrite = false; }
        Assert.True((await client.ReleaseAsync()).Success);
        Assert.All(demo.DeviceIds, id => Assert.Empty(new HubLightingRecoveryJournal(dir.Path, id).Pending));
    }
}
