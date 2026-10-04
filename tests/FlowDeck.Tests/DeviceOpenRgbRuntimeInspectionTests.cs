using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceOpenRgbRuntimeInspectionTests
{
    [Theory]
    [InlineData("absent")] [InlineData("root-file")] [InlineData("missing")] [InlineData("changed")] [InlineData("extra")] [InlineData("valid")]
    public async Task FileVerificationDistinguishesAbsenceAndDamageWithoutExecutingAnything(string scenario)
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true);
        if (scenario is not ("absent" or "root-file")) f.Package.Prepare(f.Zip,f.Target);
        if (scenario == "root-file") File.WriteAllText(f.Target,"not a directory");
        if (scenario == "missing") File.Delete(Path.Combine(f.Target,"OpenRGB.exe"));
        if (scenario == "changed") File.AppendAllText(Path.Combine(f.Target,"OpenRGB.exe"),"changed");
        if (scenario == "extra") File.WriteAllText(Path.Combine(f.Target,"injected.dll"),"not pinned");
        var before = Directory.GetFiles(f.Root,"*",SearchOption.AllDirectories).ToDictionary(p => p,p => File.ReadAllBytes(p));
        var result = await new DeviceOpenRgbRuntimeInspector(f.Target,f.Package,false).InspectAsync(default);
        Assert.Equal(scenario == "absent" ? OpenRgbRuntimeInspectionStatus.NotInstalled :
            scenario == "valid" ? OpenRgbRuntimeInspectionStatus.Verified : OpenRgbRuntimeInspectionStatus.Invalid,result.Status);
        Assert.False(result.StartupAccepted); Assert.Equal(f.Definition.Release,result.ExpectedVersion);
        Assert.True(DeviceOpenRgbRuntimeInspection.Valid(result));
        Assert.Equal(before.Count,Directory.GetFiles(f.Root,"*",SearchOption.AllDirectories).Length);
        foreach (var (path,bytes) in before) Assert.Equal(bytes,File.ReadAllBytes(path));
        Assert.Throws<IOException>(() => OpenRgbOwnedProcess.RequireAcceptedDetection(OpenRgbStartupKind.AsrockPolychromeUsb));
    }

    [Fact]
    public async Task LockedFileIsNotReportedAsInstalledAndInspectionCanBeRetried()
    {
        using var f = new OpenRgbRuntimePackageTests.Fixture(helper:true); f.Package.Prepare(f.Zip,f.Target);
        var inspector = new DeviceOpenRgbRuntimeInspector(f.Target,f.Package,false);
        using (var locked = new FileStream(Path.Combine(f.Target,"OpenRGB.exe"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            var value = await inspector.InspectAsync(default);
            Assert.Equal(OpenRgbRuntimeInspectionStatus.Invalid,value.Status); Assert.False(value.StartupAccepted);
            Assert.DoesNotContain(f.Root,JsonSerializer.Serialize(value,Contract.Json));
        }
        Assert.Equal(OpenRgbRuntimeInspectionStatus.Verified,(await inspector.InspectAsync(default)).Status);
    }

    [Fact]
    public async Task CancelledInspectionDoesNotEvenInspectANonexistentDirectory()
    {
        using var dir = new ServiceTestDirectory(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var inspector = new DeviceOpenRgbRuntimeInspector(Path.Combine(dir.Path,"absent"),OpenRgbRuntimePackage.FlowDeckPinned,false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync(cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("status")] [InlineData("startup")] [InlineData("version-null")] [InlineData("version-path")] [InlineData("version-size")]
    public void InvalidStatusCannotClaimAcceptanceOrExposePaths(string invalid)
    {
        var value = new DeviceOpenRgbRuntimeInspection(OpenRgbRuntimeInspectionStatus.NotInstalled,false,"1.0-flowdeck-patch8");
        value = invalid switch
        {
            "status" => value with { Status = (OpenRgbRuntimeInspectionStatus)99 },
            "startup" => value with { StartupAccepted = true },
            "version-null" => value with { ExpectedVersion = null! },
            "version-path" => value with { ExpectedVersion = @"C:/Users/private" },
            _ => value with { ExpectedVersion = new('x',65) }
        };
        Assert.False(DeviceOpenRgbRuntimeInspection.Valid(value));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationReply(Outcome.Confirmed,"result",Guid.NewGuid().ToString("N"),OpenRgbRuntime:value)));
    }

    [Fact]
    public void ProtocolAllowsOnlyGenerationForInspectionAndOnlyOneKindOfReply()
    {
        var generation = Guid.NewGuid().ToString("N");
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.InspectOpenRgbRuntime,ExpectedGeneration:generation);
        Assert.True(DeviceConfigurationProtocol.Valid(request));
        foreach (var invalid in new[] {request with { OpenRgbPort = 6742 },request with { Data = [1] },request with { TopologyRevision = 0 },
            request with { TransferId = generation },request with { ExpectedGeneration = null }})
            Assert.False(DeviceConfigurationProtocol.Valid(invalid));
        var reply = new DeviceConfigurationReply(Outcome.Confirmed,"result",generation,OpenRgbRuntime:new(OpenRgbRuntimeInspectionStatus.Verified,false,"1.0"));
        Assert.True(DeviceConfigurationProtocol.Valid(reply));
        foreach (var invalid in new[] { reply with { Data = [1] }, reply with { TransferId = generation }, reply with { Candidates = [] },
            reply with { Outcome = Outcome.Failed }, reply with { ChangedComponents = [] },
            reply with { OpenRgbInspection = new(6742,OpenRgbInspectionStatus.Observed,"SDK",[new("x","x","x",OpenRgbInspectionCondition.Observed)]) } })
            Assert.False(DeviceConfigurationProtocol.Valid(invalid));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuthenticatedClientChecksFeatureAndDoesNotTouchSdkSettingsOrOutputs(bool enabled)
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path,openRgbRuntimeInspection:enabled);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var generation = client.Snapshot!.Configuration.Generation;
        var configuration = JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration,Contract.Json);
        Assert.Equal(enabled,client.SupportsOpenRgbRuntimeInspection);
        if(enabled) Assert.Equal(demo.OpenRgbRuntime.Result,await client.InspectOpenRgbRuntimeAsync());
        else await Assert.ThrowsAsync<IOException>(() => client.InspectOpenRgbRuntimeAsync());
        await client.RefreshAsync(readConfiguration:true);
        Assert.Equal(generation,client.Snapshot!.Configuration.Generation);
        Assert.Equal(configuration,JsonSerializer.Serialize(client.Snapshot.Configuration.Configuration,Contract.Json));
        Assert.Equal(enabled ? 1 : 0,demo.OpenRgbRuntime.Calls); Assert.Equal(0,demo.OpenRgb.Calls);
        Assert.All(demo.Hubs,h => Assert.Equal(0,h.Writes));
    }

    [Fact]
    public async Task CallerCancellationAfterDispatchKeepsTheSharedControlSessionConnected()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var client = demo.CreateClient(); await client.ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demo.OpenRgbRuntime.Before = async ct => { entered.SetResult(); await finish.Task.WaitAsync(ct); };
        using var cancel = new CancellationTokenSource(); var session = client.Snapshot!.Session;
        var pending = client.InspectOpenRgbRuntimeAsync(cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            cancel.Cancel(); finish.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await client.RefreshAsync(); Assert.True(client.Snapshot!.Connected); Assert.Equal(session,client.Snapshot.Session);
            Assert.Equal(0,demo.OpenRgb.Calls); Assert.All(demo.Hubs,h => Assert.Equal(0,h.Writes));
        }
        finally { finish.TrySetResult(); }
    }
}

public partial class DeviceConfigurationAuthorityTests
{
    [Fact]
    public async Task RuntimeFileInspectionKeepsCoolingPersistenceAvailableAndRejectsItsNowStaleReply()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspector = new DemoOpenRgbRuntimeInspector { Before = async ct => { entered.SetResult(); await finish.Task.WaitAsync(ct); } };
        await using var r = await Rig.Create(runtimeInspector:inspector);
        var request = r.Request(new(DeviceConfigurationOperation.InspectOpenRgbRuntime,ExpectedGeneration:r.Startup.Generation.Id));
        var pending = r.Startup.Runtime.ExecuteAsync(request); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var duplicate = r.Startup.Runtime.ExecuteAsync(request);
            Assert.Contains(DeviceOpenRgbRuntimeInspection.Feature,r.Hello.Features);
            var busy = await r.Send(request.Configuration!).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(busy.Success); Assert.Equal(1,inspector.Calls);
            var cooling = await r.Startup.Runtime.ExecuteAsync(r.Duty(75)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(cooling.Success,cooling.Error); Assert.Equal(75,(await r.Hub.ReadAsync()).Channels[0].Duty);
            finish.TrySetResult(); var reply = await pending; Assert.False(reply.Success); Assert.Null(reply.Configuration);
            Assert.Equal(reply,await duplicate); Assert.Equal(1,inspector.Calls);
        }
        finally { finish.TrySetResult(); await pending; }
    }

    [Fact]
    public async Task RuntimeInspectionPreservesUploadsAndRejectsOldSessionsBeforeAnyFileRead()
    {
        var inspector = new DemoOpenRgbRuntimeInspector(); await using var r = await Rig.Create(runtimeInspector:inspector);
        var transfer = await r.Upload(r.Changed(),validate:false); var generation = r.Startup.Generation;
        var request = new DeviceConfigurationRequest(DeviceConfigurationOperation.InspectOpenRgbRuntime,ExpectedGeneration:generation.Id);
        Assert.False((await r.Send(request with { ExpectedGeneration = Guid.NewGuid().ToString("N") })).Success);
        Assert.False((await r.Startup.Runtime.ExecuteAsync(r.Request(request) with { Session = Guid.NewGuid().ToString("N") })).Success);
        Assert.Equal(0,inspector.Calls);
        Assert.True((await r.Send(request)).Success); Assert.Equal(1,inspector.Calls); Assert.Equal(generation,r.Store.ReadActivePointer());
        Assert.Equal(0,r.Hub.Writes); Assert.True((await r.Send(new(DeviceConfigurationOperation.Validate,transfer))).Success);
    }

    [Fact]
    public async Task DisconnectCancelsInFlightRuntimeInspectionWithoutReturningOldReadiness()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspector = new DemoOpenRgbRuntimeInspector { Before = async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite,ct); } };
        await using var r = await Rig.Create(runtimeInspector:inspector);
        var pending = r.Send(new(DeviceConfigurationOperation.InspectOpenRgbRuntime,ExpectedGeneration:r.Startup.Generation.Id));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await r.Startup.Runtime.DisconnectAsync(r.Hello.Session);
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(reply.Success); Assert.Null(reply.Configuration); Assert.Equal(0,r.Hub.Writes);
    }
}
