using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class DeviceProviderReplacementTests
{
    [Fact]
    public async Task PreviewDescribesActualHandleOwnershipAndDoesNotStopControlOrStageSettings()
    {
        await using var r = await RoutingRig.Create(); var before = r.Startup.Generation;
        var releases = r.Direct.Driver.Releases; var polls = r.Direct.Polls;
        var id = await r.Upload(r.Target(), validate: false);
        var request = r.Request(new(DeviceConfigurationOperation.ValidateProviderRoute, id));
        var reply = await r.Startup.Runtime.ExecuteAsync(request); Assert.True(reply.Success, reply.Error);
        var review = reply.Configuration!.ProviderRoute!; Assert.True(DeviceProviderRouteReview.Valid(review));
        Assert.Equal(FixtureRoutingDefinition.Id, review.PolicyId); Assert.Equal(FixtureRoutingDefinition.Digest, review.PolicyDigest);
        Assert.Equal(["direct"], review.RemoveProviderIds); Assert.Equal(["sdk"], review.AddProviderIds);
        Assert.Equal(["independent"], review.RetainedProviderIds); Assert.Equal(["rgb"], review.RemovingDeviceIds);
        Assert.True(review.StopsAllControls); Assert.Equal(r.Startup.Runtime.State().Inventory.TopologyRevision, review.TopologyRevision);
        Assert.Equal(releases, r.Direct.Driver.Releases); Assert.Equal(polls, r.Direct.Polls);
        Assert.Equal(before, r.Store.ReadActivePointer()); Assert.False(File.Exists(r.RecordPath)); Assert.Equal(2, r.Policy.Created.Count);
        // A caller cannot mutate the retained review, and retrying the same request returns its original reply.
        review.RemoveProviderIds[0] = "independent";
        var retry = await r.Startup.Runtime.ExecuteAsync(request);
        Assert.Equal(["direct"], retry.Configuration!.ProviderRoute!.RemoveProviderIds);
        Assert.True((await r.Apply(id)).Success);
    }

    [Fact]
    public async Task ChangedPlanBetweenReviewAndApplyIsRejectedBeforeAnyReleaseOrSettingsStage()
    {
        await using var r = await RoutingRig.Create(); var id = await r.Upload(r.Target());
        var releases = r.Direct.Driver.Releases + r.Independent.Driver.Releases; var before = r.Startup.Generation;
        var files = Directory.GetFiles(r.Store.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
        r.Policy.Planner = (_, _) => new(["direct", "independent"], ["sdk"]);
        var result = await r.Apply(id); Assert.False(result.Success); Assert.Contains("再確認", result.Error);
        Assert.Equal(releases, r.Direct.Driver.Releases + r.Independent.Driver.Releases); Assert.False(r.Direct.Disposed);
        Assert.Equal(before, r.Store.ReadActivePointer()); Assert.Equal(files, Directory.GetFiles(r.Store.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Null(r.Journal.Pending); Assert.Equal(2, r.Policy.Created.Count);
        r.Policy.Planner = null; Assert.False((await r.Apply(id)).Success); // An invalidated review cannot revive.
        Assert.True((await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id))).Success);
        Assert.True((await r.Apply(id)).Success);
    }

    [Fact]
    public async Task FailedRevalidationCannotLeaveAnEarlierSuccessfulReviewApplicable()
    {
        await using var r = await RoutingRig.Create(); var id = await r.Upload(r.Target()); var releases = r.Direct.Driver.Releases;
        r.Policy.Reject = true; Assert.False((await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id))).Success);
        r.Policy.Reject = false; Assert.False((await r.Apply(id)).Success);
        Assert.Equal(releases, r.Direct.Driver.Releases); Assert.False(File.Exists(r.RecordPath));
    }

    [Fact]
    public async Task LegacyValidationCannotAuthorizeRouteChangesOrReceiveNewReplyFields()
    {
        await using var r = await RoutingRig.Create(); var id = await r.Upload(r.Target(), validate: false);
        var result = await r.Send(new(DeviceConfigurationOperation.Validate, id)); Assert.False(result.Success);
        Assert.False((await r.Apply(id)).Success); Assert.False(File.Exists(r.RecordPath));
        id = await r.Upload(r.Startup.Configuration, validate: false);
        result = await r.Send(new(DeviceConfigurationOperation.Validate, id)); Assert.True(result.Success);
        Assert.Null(result.Configuration!.ProviderRoute);
        Assert.DoesNotContain("providerRoute", JsonSerializer.Serialize(result.Configuration, Contract.Json));
    }

    [Fact]
    public async Task AServiceWithoutCompiledRoutingDoesNotAdvertiseOrAcceptTheReviewOperation()
    {
        await using var r = await RoutingRig.Create(routes: false);
        Assert.DoesNotContain(DeviceProviderRouteReview.Feature, r.Hello.Features);
        var id = await r.Upload(r.Target(), validate: false);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id))).Success);
        Assert.False(r.Direct.Disposed); Assert.False(File.Exists(r.RecordPath));
    }

    [Theory]
    [InlineData("missing-source")] [InlineData("retained-collision")] [InlineData("too-many")]
    public async Task PreviewRejectsPlansThatCannotBeAppliedToRegisteredProviders(string invalid)
    {
        await using var r = await RoutingRig.Create();
        r.Policy.Planner = (_, _) => invalid switch {
            "missing-source" => new(["missing"], ["sdk"]),
            "retained-collision" => new(["direct"], ["independent"]),
            _ => new([], Enumerable.Range(0, 8).Select(i => "sdk" + i).ToArray()) };
        var id = await r.Upload(r.Target(), validate: false); var result = await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id));
        Assert.False(result.Success); Assert.False((await r.Apply(id)).Success); Assert.False(File.Exists(r.RecordPath));
        Assert.Equal(2, r.Policy.Created.Count); Assert.False(r.Direct.Disposed);
    }

    [Fact]
    public async Task ChangedTopologyCannotBeBlessedByPassingItsNewRevisionAtApply()
    {
        await using var r = await RoutingRig.Create(); var id = await r.Upload(r.Target()); var releases = r.Direct.Driver.Releases;
        r.Independent.Driver.Disposed = true; _ = r.Startup.Runtime.State();
        try { Assert.False((await r.Apply(id)).Success); Assert.Equal(releases, r.Direct.Driver.Releases); Assert.False(File.Exists(r.RecordPath)); }
        finally { r.Independent.Driver.Disposed = false; }
    }

    [Theory]
    [InlineData("policy")] [InlineData("digest")] [InlineData("duplicate")] [InlineData("count")] [InlineData("stop")] [InlineData("collision")]
    public void ReviewContractRejectsAmbiguousOrUnboundedPlans(string invalid)
    {
        var review = new DeviceProviderRouteReview("policy", 1, new('a', 64), ["old"], ["new"], ["sensors"], 0, ["rgb"]);
        Assert.True(DeviceProviderRouteReview.Valid(review));
        var broken = invalid switch {
            "policy" => review with { PolicyRevision = 0 }, "digest" => review with { PolicyDigest = "invalid" },
            "duplicate" => review with { RemovingDeviceIds = ["rgb", "rgb"] },
            "count" => review with { RemovingDeviceIds = Enumerable.Range(0, 33).Select(i => "d" + i).ToArray() },
            "stop" => review with { StopsAllControls = false }, _ => review with { AddProviderIds = ["sensors"] } };
        Assert.False(DeviceProviderRouteReview.Valid(broken));
    }

    [Fact]
    public void ReviewReplyCannotBeMixedWithInspectionOrTransferChunks()
    {
        var id = Guid.NewGuid().ToString("N");
        var review = new DeviceProviderRouteReview("policy", 1, new('a', 64), ["old"], ["new"], [], 1, []);
        var reply = new DeviceConfigurationReply(Outcome.Confirmed, "review", id, id, ChangedComponents: ["preferences.json"], Bindings: [], ProviderRoute: review);
        Assert.True(DeviceConfigurationProtocol.Valid(reply));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Data = [1] }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Outcome = Outcome.Failed }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { TransferId = null }));
        Assert.False(DeviceConfigurationProtocol.Valid(reply with { Bindings = null }));
        Assert.True(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.ValidateProviderRoute, id)));
        Assert.False(DeviceConfigurationProtocol.Valid(new DeviceConfigurationRequest(DeviceConfigurationOperation.ValidateProviderRoute, id, TopologyRevision: 1)));
    }

    [Theory]
    [InlineData("index")] [InlineData("duplicate")] [InlineData("large")]
    public void ReviewRejectsInvalidSelectedDeviceIdentities(string invalid)
    {
        var id = "openrgb-sdk:" + new string('a', 64);
        var values = invalid switch { "index" => new[] { "41" }, "duplicate" => [id, id],
            _ => Enumerable.Range(0, 33).Select(i => "openrgb-sdk:" + i.ToString("x64")).ToArray() };
        Assert.False(DeviceProviderRouteReview.Valid(new("policy", 1, new('a', 64), ["sdk"], ["sdk"], [], 1, [], OpenRgbDeviceIds: values)));
    }

    [Fact]
    public async Task LegacyReviewOmitsSelectionFieldsEvenWhenTheServiceSupportsSelection()
    {
        await using var r = await RoutingRig.Create(selection: true);
        Assert.Contains(DeviceProviderRouteReview.SelectionFeature, r.Hello.Features);
        var id = await r.Upload(r.Target(), validate: false);
        var legacy = await r.Send(new(DeviceConfigurationOperation.ValidateProviderRoute, id)); Assert.True(legacy.Success, legacy.Error);
        Assert.Null(legacy.Configuration!.ProviderRoute!.OpenRgbDeviceIds);
        Assert.DoesNotContain("openRgbDeviceIds", JsonSerializer.Serialize(legacy.Configuration, Contract.Json));
        Assert.True((await r.Apply(id)).Success);
        id = await r.Upload(r.Target(false), validate: false);
        var modern = await r.Send(new(DeviceConfigurationOperation.ValidateProviderSelection, id)); Assert.True(modern.Success, modern.Error);
        Assert.Empty(modern.Configuration!.ProviderRoute!.OpenRgbDeviceIds!);
        Assert.True((await r.Apply(id)).Success);
    }

    [Fact]
    public async Task APolicyWithoutDeviceSelectionDoesNotAdvertiseOrAcceptItsReviewOperation()
    {
        await using var r = await RoutingRig.Create();
        Assert.DoesNotContain(DeviceProviderRouteReview.SelectionFeature, r.Hello.Features);
        var id = await r.Upload(r.Target(), validate: false);
        Assert.False((await r.Send(new(DeviceConfigurationOperation.ValidateProviderSelection, id))).Success);
        Assert.False(r.Direct.Disposed); Assert.False(File.Exists(r.RecordPath));
    }
}
