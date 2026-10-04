using FlowDeck.Core;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OwnedShutdownSignalStopsRealNativeServerAndClosesControllerAfterSdkRestoration(bool failFirst)
    {
        await using var r = new NativeSdkRig(fullServer: true, ownedShutdown: true);
        var refuse = failFirst; var releases = 0;
        await r.Open(release: async () =>
        {
            releases++;
            if (refuse) throw new IOException("fixture restoration pending");
            SdkAccepted(await r.Executor.RecoverAsync(r.Native.Scope.CheckRoute));
            await r.Connection.DisposeAsync();
        });
        SdkAccepted(await r.Executor.ApplyAsync(NativeSdkBlue, new(LightingExitAction.Off), r.Native.Scope.CheckRoute));
        var owner = r.Native.Owner!;
        if (failFirst)
        {
            await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
            Assert.False(owner.ResourcesClosed); Assert.False(r.Native.RealChild!.HasExited);
            Assert.False(File.Exists(Path.Combine(r.Native.Directory.Path, "native-server-stopped.json")));
            // The failed disposal intentionally rejects new user work, but its next restoration
            // callback can still use the authenticated native channel.
            refuse = false;
        }
        await owner.DisposeAsync();
        Assert.True(owner.ResourcesClosed); Assert.True(owner.GracefulExitConfirmed); Assert.False(owner.ForcedTerminationRequired);
        Assert.Null(owner.GracefulShutdownFailure); Assert.Equal(failFirst ? 2 : 1, releases);
        var result = await r.ReadFile("native-result.json", checkError: true);
        Assert.Equal(0, result.GetProperty("code").GetInt32());
        var server = await r.ReadFile("native-server-stopped.json");
        Assert.False(server.GetProperty("online").GetBoolean()); Assert.Equal(0, server.GetProperty("clients").GetInt32());
        Assert.Equal(1, (await r.ReadFile("native-state.json")).GetProperty("closes").GetInt32());
        Assert.Null(r.PersistedSdk); Assert.False(File.Exists(Path.Combine(r.Native.Directory.Path,"native-finish")));
        await r.Stop(); Assert.True(r.Native.Scope.ResourcesClosed);
    }
}
