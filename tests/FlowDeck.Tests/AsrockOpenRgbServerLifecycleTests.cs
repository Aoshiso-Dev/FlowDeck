using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlowDeck.Tests;

public sealed partial class AsrockOpenRgbDetectionScopeTests
{
    [Fact]
    public async Task FullServerLifecycleBarriersJoinOwnedWorkAndReleaseDisconnectedClients()
    {
        using var directory = new ServiceTestDirectory();
        using var resource = typeof(AsrockOpenRgbDetectionScopeTests).Assembly
            .GetManifestResourceStream("FlowDeck.Tests.AsrockFullServerIntegration.bin")!;
        using var image = new MemoryStream();
        await resource.CopyToAsync(image);
        var bytes = image.ToArray();
        Assert.Equal(NativeSdkRig.FullServerSha256, Convert.ToHexString(SHA256.HashData(bytes)));
        var exe = Path.Combine(directory.Path, "authorization-client.exe");
        await File.WriteAllBytesAsync(exe, bytes);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.Path
        };
        start.ArgumentList.Add("--lifecycle-fixture");
        // This entry point never initializes bootstrap, HID, detection, Qt or ResourceManager.
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(process.ExitCode == 0, await errors);
            using var result = JsonDocument.Parse(await output);
            Assert.False(result.RootElement.GetProperty("hardwareAccessed").GetBoolean());
            Assert.Equal(11, result.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(new[]
            {
                "startup-retains-concurrent-controller-publication",
                "stop-joins-accepted-before-adoption", "restart-and-idempotent-start-stop",
                "failed-bind-cleans-up-and-restarts", "partial-header-stop", "partial-payload-stop",
                "disconnected-inflight-profile-request-retains-client", "stop-joins-controller-request",
                "pending-controller-request-is-canceled-and-released", "stop-joins-notification-send",
                "96-disconnects-release-receiver-and-sender"
            }, result.RootElement.GetProperty("passed").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AsrockAuthorizationFixture.DeleteExitedImage(directory.Path);
        }
    }
}
