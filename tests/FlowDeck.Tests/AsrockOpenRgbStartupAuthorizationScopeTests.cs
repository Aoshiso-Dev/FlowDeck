using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    [Fact] public async Task DetectionCanRunInsideResumeBeforeStartupReturnsAndFinalAttachmentHappensOnlyOnce()
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default);
        var channel = new MockOpenRgbStartupChannel([]) { OnBind = r.Scope.AttachPreparedHelper };
        r.Resuming = r.Detect;
        var owner = await r.Start(channel: channel);
        r.Scope.AttachHelper(owner); r.Scope.CheckInventory(r.Inventory());
        Assert.Throws<IOException>(() => r.Scope.AttachHelper(owner));
        Assert.Throws<IOException>(() => r.Scope.AttachPreparedHelper(owner));
        Assert.Contains(owner.ProcessId, r.ConflictPids);
    }

    [Fact] public async Task PreparedOwnershipPermitsConflictChecksButCannotAuthorizeBeforeResume()
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default);
        var channel = new MockOpenRgbStartupChannel([]) { OnBind = owner =>
        {
            r.Scope.AttachPreparedHelper(owner); r.Scope.CheckRoute();
            var io = new AsrockOpenRgbIoReceipt(0, 0, 0, false, -2, -2, new byte[65], new byte[64]);
            Assert.Throws<IOException>(() => r.Scope.Authorize(new(1, AsrockAuthorizationPhase.BeforeOpen, r.Boards[0].Path,
                new(false, 0, 0, 0, 0, 0), io), default));
            Assert.Throws<IOException>(() => r.Scope.AttachHelper(owner));
        } };
        r.Scope.AttachHelper(await r.Start(channel: channel)); r.Detect(); r.Scope.CheckInventory(r.Inventory());
    }

    [Fact] public async Task PreparedOwnershipCannotBeInstalledAfterExecutionStarted()
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default);
        var owner = await r.Start(); Assert.Throws<IOException>(() => r.Scope.AttachPreparedHelper(owner));
        r.Scope.AttachHelper(owner); r.Detect(); r.Scope.CheckInventory(r.Inventory());
    }

    [Fact] public async Task SdkInventoryCannotAdoptAHelperWhoseDetectionSequenceHasNotCompleted()
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default); r.Scope.AttachHelper(await r.Start());
        Assert.Throws<IOException>(() => r.Scope.CheckInventory(r.Inventory()));
        Assert.Empty(r.Boards[0].Device.Writes);
        r.Detect(); r.Scope.CheckInventory(r.Inventory());
    }
    [Fact] public async Task StartupPermissionRequiresTheCapturedAndAttachedHelper()
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default);
        var io = new AsrockOpenRgbIoReceipt(0, 0, 0, false, -2, -2, new byte[65], new byte[64]);
        Assert.Throws<IOException>(() => r.Scope.Authorize(new(1, AsrockAuthorizationPhase.BeforeOpen, r.Boards[0].Path,
            new(false, 0, 0, 0, 0, 0), io), default));
        Assert.Empty(r.Boards[0].Device.Writes);
    }
    [Fact] public async Task PhysicalContinuityIsRecheckedInsideEachStartupRequest()
    {
        await using var r = new Rig(); await r.Scope.PrepareAsync(default); r.Scope.AttachHelper(await r.Start());
        var emulator = new AsrockDetectionEmulator(); var board = r.Boards[0]; board.Device.OwnerThread = null;
        Assert.Throws<IOException>(() => emulator.Execute(board.Path, board.Device.Available, board.Device.Counts, board.Device.Order,
            (request, ct) =>
            {
                if (request.Phase == AsrockAuthorizationPhase.BeforeWrite) board.Watch.Revoked = true;
                r.Scope.Authorize(request, ct);
            }, board.Device.Exchange));
        Assert.Empty(board.Device.Writes); r.AllowUnrestored = true;
    }
}
