using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class DeviceDiagnosticHardwareTests
{
    private sealed class Driver : IDeviceControlDriver, IDeviceDiagnosticHardwareSource
    {
        public readonly GenericTestDriver Inner = new("PRIVATE-serial") { Locator = "PRIVATE-path" };
        public int Reads;
        public bool Fail;
        public string DeviceId => Inner.DeviceId;
        public DeviceObservation? Observe() => Inner.Observe();
        public string? Validate(DeviceEndpointAction action) => Inner.Validate(action);
        public Task<DeviceDriverResult> ApplyAsync(DeviceEndpointAction action, CancellationToken ct) => Inner.ApplyAsync(action, ct);
        public Task<DeviceDriverReleaseResult[]> ReleaseAsync(DeviceReleaseReason reason, CancellationToken ct) => Inner.ReleaseAsync(reason, ct);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public DeviceDiagnosticHardware ReadCachedDiagnosticHardware()
        {
            Reads++; if (Fail) throw new IOException(@"C:\PRIVATE\secret");
            return new(DeviceDiagnosticReadState.Available, [
                new(DeviceDiagnosticHardwareKind.UsbProduct, DeviceDiagnosticReadState.Available, VendorId: 0x1E71, ProductId: 0x2022),
                new(DeviceDiagnosticHardwareKind.Firmware, DeviceDiagnosticReadState.Available, Value: "1.2.3")]);
        }
    }
    [Fact] public async Task HardwareExportIsExplicitBoundToItsTransferAndUsesOnlyTheCachedSource()
    {
        var driver = new Driver(); await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog());
        var hello = runtime.OpenSession("user"); long sequence = 0;
        DeviceServiceRequest Query(DeviceDiagnosticQuery query) => new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), ++sequence,
            "PC", DeviceOperation.ReadDiagnostics, Diagnostics: query);
        Assert.Contains(DeviceDiagnosticHardwareRules.Feature, hello.Features);
        var old = await runtime.ExecuteAsync(Query(new(DeviceDiagnosticScope.Summary)));
        var oldReport = JsonSerializer.Deserialize<DeviceDiagnosticReport>(old.Diagnostics!.Data, Contract.Json)!;
        Assert.Equal(1, oldReport.FormatVersion); Assert.Null(Assert.Single(oldReport.Devices).Hardware); Assert.Equal(0, driver.Reads);
        Assert.DoesNotContain("\"hardware\"", System.Text.Encoding.UTF8.GetString(old.Diagnostics.Data));
        Assert.DoesNotContain("includeHardware", JsonSerializer.Serialize(new DeviceDiagnosticQuery(), Contract.Json));
        var fresh = await runtime.ExecuteAsync(Query(new(DeviceDiagnosticScope.Summary, IncludeHardware: true)));
        Assert.True(fresh.Success, fresh.Error);
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(fresh.Diagnostics!.Data, Contract.Json)!;
        Assert.Equal(2, report.FormatVersion); Assert.Equal(2, Assert.Single(report.Devices).Hardware!.Items.Length);
        Assert.Equal(1, driver.Reads); Assert.Equal(0, driver.Inner.Writes); Assert.Equal(0, driver.Inner.Releases);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(report, Contract.Json));
        Assert.False((await runtime.ExecuteAsync(Query(new(DeviceDiagnosticScope.Summary, fresh.Diagnostics.TransferId)))).Success);
        Assert.True((await runtime.ExecuteAsync(Query(new(DeviceDiagnosticScope.Summary, fresh.Diagnostics.TransferId, IncludeHardware: true)))).Success);
        Assert.Equal(1, driver.Reads);
        driver.Inner.Connected = false; runtime.Refresh();
        var missing = await runtime.ExecuteAsync(Query(new(DeviceDiagnosticScope.Summary, IncludeHardware: true)));
        var disconnected = Assert.Single(JsonSerializer.Deserialize<DeviceDiagnosticReport>(missing.Diagnostics!.Data, Contract.Json)!.Devices);
        Assert.False(disconnected.Connected); Assert.Equal(DeviceDiagnosticReadState.Missing, disconnected.Hardware!.State);
        Assert.Empty(disconnected.Hardware.Items); Assert.Equal(1, driver.Reads);
    }
    [Fact] public async Task SourceFailureIsLocalAndDoesNotPreventAnAuthenticatedExport()
    {
        var driver = new Driver { Fail = true }; await using var runtime = new DeviceControlRuntime([driver, new GenericTestDriver("other")], ["user"], new TestLog());
        var hello = runtime.OpenSession("user");
        var reply = await runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC",
            DeviceOperation.ReadDiagnostics, Diagnostics: new(IncludeHardware: true)));
        Assert.True(reply.Success, reply.Error);
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(reply.Diagnostics!.Data, Contract.Json)!;
        Assert.Contains(report.Devices, d => d.Hardware!.State == DeviceDiagnosticReadState.Failed);
        Assert.Contains(report.Devices, d => d.Hardware!.State == DeviceDiagnosticReadState.Unsupported);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(report, Contract.Json));
    }
    [Fact] public async Task ClientNegotiatesHardwareAndFallsBackToTheOriginalReportWithoutTheFeature()
    {
        using var dir = new ServiceTestDirectory(); await using var demo = await DemoDeviceWorkspace.CreateAsync(dir.Path);
        await using var transport = DeviceServicePipeClient.ForDemo(demo.PipeName, Environment.ProcessId);
        await using var client = new DeviceWorkspaceClient(transport); await client.ConnectAsync();
        var fresh = await client.ReadDiagnosticsAsync(DeviceDiagnosticScope.Summary);
        Assert.Equal(2, fresh.Service.FormatVersion); Assert.All(fresh.Service.Devices, d => Assert.NotNull(d.Hardware));
        var index = Array.IndexOf(transport.Hello!.Features, DeviceDiagnosticHardwareRules.Feature);
        Assert.True(index >= 0); transport.Hello.Features[index] = "unsupported-hardware-test";
        var old = await client.ReadDiagnosticsAsync(DeviceDiagnosticScope.Summary);
        Assert.Equal(1, old.Service.FormatVersion); Assert.All(old.Service.Devices, d => Assert.Null(d.Hardware));
    }
    [Theory] [InlineData("path")] [InlineData("newline")] [InlineData("bounds")] [InlineData("extra")]
    [InlineData("bad-enum")] [InlineData("unavailable")]
    public void InvalidOrUnavailableItemsCannotSmuggleFreeFormFields(string reason)
    {
        var item = new DeviceDiagnosticHardwareItem(DeviceDiagnosticHardwareKind.Firmware, DeviceDiagnosticReadState.Available, Value: "1.2.3");
        item = reason switch {
            "path" => item with { Value = @"C:\PRIVATE\1.2" }, "newline" => item with { Value = "1.2\nPRIVATE" },
            "bounds" => item with { Kind = DeviceDiagnosticHardwareKind.UsbProduct, VendorId = 65536, ProductId = 1, Value = "PRIVATE" },
            "extra" => item with { Kind = DeviceDiagnosticHardwareKind.UsbDeviceRevision, Revision = 256, Value = "PRIVATE" },
            "bad-enum" => item with { Evidence = (DeviceDiagnosticHardwareEvidence)999, Value = "PRIVATE" },
            _ => item with { State = DeviceDiagnosticReadState.NotObserved, Value = "PRIVATE" } };
        var result = DeviceDiagnosticHardwareRules.Normalize(new(DeviceDiagnosticReadState.Available, [item]));
        Assert.Equal(reason == "unavailable" ? DeviceDiagnosticReadState.NotObserved : DeviceDiagnosticReadState.Invalid, Assert.Single(result.Items).State);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, Contract.Json));
    }
    [Fact] public void MalformedAndOversizedSourcesAreBoundedWithoutTruncatingRawValuesIntoValidOnes()
    {
        var item = new DeviceDiagnosticHardwareItem(DeviceDiagnosticHardwareKind.Model, DeviceDiagnosticReadState.Available, Value: new('a', 161));
        Assert.Equal(DeviceDiagnosticReadState.Invalid, Assert.Single(DeviceDiagnosticHardwareRules.Normalize(new(DeviceDiagnosticReadState.Available, [item])).Items).State);
        foreach (var input in new DeviceDiagnosticHardware?[] { null, new(DeviceDiagnosticReadState.Available, null!),
            new(DeviceDiagnosticReadState.Available, Enumerable.Repeat(item, 25).ToArray()), new(DeviceDiagnosticReadState.Available, [null!]),
            new((DeviceDiagnosticReadState)999, []), new(DeviceDiagnosticReadState.Available, [item with { Kind = (DeviceDiagnosticHardwareKind)999 }]) })
            Assert.Equal(DeviceDiagnosticReadState.Invalid, DeviceDiagnosticHardwareRules.Normalize(input).State);
        Assert.Empty(DeviceDiagnosticHardwareRules.Normalize(new(DeviceDiagnosticReadState.Failed, [item])).Items);
    }
    [Theory] [InlineData(@"PCI\VEN_10DE&DEV_2B85&SUBSYS_98761234\PRIVATE-INSTANCE", 0x10DE, 0x2B85)]
    [InlineData(@"pci\ven_1002&dev_164E\PRIVATE", 0x1002, 0x164E)]
    public void PciMetadataRetainsOnlyNumericProductIds(string identity, int vendor, int product)
    {
        var result = HardwareSensorDiagnostics.Pci(identity);
        Assert.Equal(DeviceDiagnosticReadState.Available, result.State); Assert.Equal(vendor, result.VendorId); Assert.Equal(product, result.ProductId);
        Assert.Null(result.Value); Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, Contract.Json));
    }
    [Theory] [InlineData(@"C:\PRIVATE\file")] [InlineData(@"PCI\VEN_10DE&DEV_2B85")]
    [InlineData(@"PCI\VEN_10DE&DEV_2B85\..\PRIVATE")]
    public void IncompleteOrArbitraryPciIdentityIsNotAProduct(string identity) =>
        Assert.Equal(DeviceDiagnosticReadState.Invalid, HardwareSensorDiagnostics.Pci(identity).State);
    [Theory] [InlineData("/lpc/nct6799d/0", "NCT6799D")] [InlineData("/lpc/it8689e/1", "IT8689E")]
    [InlineData("/lpc/f71889ad/0", "F71889AD")] [InlineData("/lpc/PRIVATE/0", null)]
    [InlineData("/lpc/nct6799d/0/PRIVATE", null)] [InlineData("/lpc/nct6799d/0\n", null)]
    public void ChipMetadataDropsTheBusAndInstanceAndRejectsUnrecognizedShapes(string raw, string? expected) =>
        Assert.Equal(expected, HardwareSensorDiagnostics.Chip(raw));
}
