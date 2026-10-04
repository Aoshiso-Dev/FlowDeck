using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

internal static class AsrockAuthorizationFixture
{
    internal const string Sha256 = "24311D37721D4BFC20D52045140B8547BBF6A0660A2D43A32558D9295BE45A91";
    internal static string Extract(string directory)
    {
        using var resource = typeof(AsrockAuthorizationFixture).Assembly.GetManifestResourceStream("FlowDeck.Tests.AsrockAuthorizationClient.bin")!;
        using var bytes = new MemoryStream(); resource.CopyTo(bytes); var image = bytes.ToArray();
        Assert.Equal(Sha256, Convert.ToHexString(SHA256.HashData(image)));
        var exe = Path.Combine(directory, "authorization-client.exe"); File.WriteAllBytes(exe, image); return exe;
    }
    internal static void DeleteExitedImage(string directory)
    {
        // Only called after confirmed child exit and handle disposal. Windows may briefly keep
        // an executable image mapped after signaling exit; never hide a persistent ACL error.
        var path = Path.Combine(directory, "authorization-client.exe");
        for (var attempt = 0; ; attempt++)
            try { File.Delete(path); return; }
            catch (Exception e) when (attempt < 19 && e is IOException or UnauthorizedAccessException &&
                (e.HResult == unchecked((int)0x80070005) || e.HResult == unchecked((int)0x80070020) || e.HResult == unchecked((int)0x80070021)))
            { Thread.Sleep(50); }
    }
}

public sealed class AsrockOpenRgbStartupChannelTests
{
    private sealed class UnexpectedScope : IOpenRgbStartupAuthorizationScope
    {
        public void AttachPreparedHelper(OpenRgbOwnedProcess owner) => throw new Exception("NoDetection must not attach a hardware scope");
        public void Authorize(AsrockAuthorizationRequest request, CancellationToken ct) => throw new Exception("NoDetection must not reuse hardware authorization");
    }

    [Fact] public async Task NoDetectionBootstrapAuthenticatesItsChildButAlwaysDeniesHardwareRequests()
    {
        await using var r = new Native(paused: true);
        await using var channel = AsrockOpenRgbStartupChannel.Create(OpenRgbStartupKind.NoDetection, null);
        await r.StartAsync(channel); File.WriteAllText(Path.Combine(r.Directory.Path, "bootstrap-begin"), "");
        Assert.Equal(new(20, 0, 0, true), await r.Read()); Assert.Throws<IOException>(r.Owner!.CheckRunning);
        await r.Owner.DisposeAsync(); Assert.True(r.Owner.ResourcesClosed);
    }

    [Fact] public void NoDetectionCannotAdoptAHardwareScope() =>
        Assert.Throws<IOException>(() => AsrockOpenRgbStartupChannel.Create(OpenRgbStartupKind.NoDetection, new UnexpectedScope()));

    [Theory] [InlineData(1)] [InlineData(99)]
    public void OtherProfilesCannotStartWithoutTheirExactAuthorizationScope(int kind) =>
        Assert.Throws<IOException>(() => AsrockOpenRgbStartupChannel.Create((OpenRgbStartupKind)kind, null));

    private sealed class Close(Action action) : IDisposable { public void Dispose() => action(); }
    private sealed record Result(int Code, int Writes, int Reads, bool EnvironmentCleared);
    private sealed class Native : IAsyncDisposable
    {
        public readonly ServiceTestDirectory Directory = new();
        public readonly ProcessStartInfo Start;
        public OpenRgbOwnedProcess? Owner;
        public IOpenRgbSuspendedProcess? Child;
        public bool ImageClosed;
        public Native(bool paused = false)
        {
            Start = new(AsrockAuthorizationFixture.Extract(Directory.Path))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory.Path };
            Start.Environment.Remove(AsrockOpenRgbStartupChannel.EnvironmentName); Start.ArgumentList.Add(paused ? "--bootstrap-paused" : "--bootstrap");
        }
        public async Task StartAsync(IOpenRgbStartupChannel? channel = null, Action? validate = null, Func<Task>? release = null)
        {
            Owner = await OpenRgbOwnedProcess.StartAsync(Start, new Close(() => ImageClosed = true), validate ?? (() => { }),
                info => Child = WindowsSuspendedProcess.Create(info), release ?? (() => Task.CompletedTask), channel is null ? null : () => channel);
        }
        public async Task<Result> Read()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var text = await File.ReadAllTextAsync(Path.Combine(Directory.Path, "bootstrap-result.json"), deadline.Token);
                    return JsonSerializer.Deserialize<Result>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                }
                catch (Exception e) when (e is IOException or JsonException) { }
                await Task.Delay(20, deadline.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Owner is not null) await Owner.DisposeAsync();
            Assert.True(Owner is null || Owner.ResourcesClosed);
            AsrockAuthorizationFixture.DeleteExitedImage(Directory.Path);
            Directory.Dispose();
        }
    }

    [Fact] public async Task RealSuspendedNativeChildReceivesItsOwnChannelAndAuthorizesItsFirstIo()
    {
        await using var r = new Native(); var requests = new List<AsrockAuthorizationRequest>(); var attached = false;
        await using var channel = new AsrockOpenRgbStartupChannel(owner =>
        {
            owner.CheckSuspended(); Assert.Throws<IOException>(owner.CheckRunning); attached = true;
        }, (request, _) => { Assert.True(attached); requests.Add(request); });
        await r.StartAsync(channel, release: () =>
        {
            Assert.False(channel.ResourcesClosed); r.Owner!.CheckRunning();
            File.WriteAllText(Path.Combine(r.Directory.Path, "bootstrap-finish"), ""); return Task.CompletedTask;
        });
        Assert.Equal(new(0, 1, 1, true), await r.Read()); r.Owner!.CheckRunning();
        Assert.Equal(new[] { AsrockAuthorizationPhase.BeforeOpen, AsrockAuthorizationPhase.BeforeWrite,
            AsrockAuthorizationPhase.BeforeRead, AsrockAuthorizationPhase.AfterRead }, requests.Select(q => q.Phase));
        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, requests.Select(q => q.Sequence));
        Assert.All(requests, q => Assert.Equal(@"\\?\hid#vid_26ce&pid_01a2#pipe-fixture", q.Path));
        await r.Owner.DisposeAsync(); Assert.True(channel.ResourcesClosed); Assert.True(r.ImageClosed);
    }

    [Theory] [InlineData(1, 0, 0)] [InlineData(2, 0, 0)] [InlineData(3, 1, 0)] [InlineData(4, 1, 1)]
    public async Task NativeBootstrapDenialStopsHardwareAndRevokesTheOwner(int denial, int writes, int reads)
    {
        await using var r = new Native(paused: true); var calls = 0;
        await using var channel = new AsrockOpenRgbStartupChannel(_ => { }, (_, _) =>
        { if (++calls == denial) throw new IOException("revoked"); });
        await r.StartAsync(channel); File.WriteAllText(Path.Combine(r.Directory.Path, "bootstrap-begin"), "");
        Assert.Equal(new(20, writes, reads, true), await r.Read());
        Assert.Equal(denial, calls); Assert.Throws<IOException>(r.Owner!.CheckRunning);
        await r.Owner.DisposeAsync(); Assert.True(r.Owner.ResourcesClosed);
    }

    [Fact] public async Task ChangedMetadataBeforeResumeTerminatesTheSuspendedChildWithoutHardwareRequests()
    {
        await using var r = new Native(); var calls = 0; var validations = 0;
        await using var channel = new AsrockOpenRgbStartupChannel(_ => { }, (_, _) => calls++);
        await Assert.ThrowsAsync<IOException>(() => r.StartAsync(channel, () =>
        {
            if (++validations == 2) r.Start.Environment[AsrockOpenRgbStartupChannel.EnvironmentName] = "{}";
        }));
        Assert.Equal(0, calls); Assert.True(channel.ResourcesClosed); Assert.True(r.ImageClosed);
        Assert.False(File.Exists(Path.Combine(r.Directory.Path, "bootstrap-result.json")));
    }

    [Fact] public async Task ExistingOrInheritedMetadataCannotBeAdopted()
    {
        await using var r = new Native(); r.Start.Environment[AsrockOpenRgbStartupChannel.EnvironmentName] = "{}";
        await using var channel = new AsrockOpenRgbStartupChannel(_ => { }, (_, _) => throw new Exception("never"));
        await Assert.ThrowsAsync<IOException>(() => r.StartAsync(channel));
        Assert.Null(r.Child); Assert.True(channel.ResourcesClosed); Assert.True(r.ImageClosed);
    }

    [Fact] public async Task BindingFailureRetainsNoRunningChildAndDoesNotResumeBootstrap()
    {
        await using var r = new Native(); var called = false;
        await using var channel = new AsrockOpenRgbStartupChannel(_ => { called = true; throw new IOException("capture changed"); }, (_, _) => throw new Exception("never"));
        await Assert.ThrowsAsync<IOException>(() => r.StartAsync(channel));
        Assert.True(called); Assert.True(channel.ResourcesClosed); Assert.True(r.ImageClosed);
        Assert.False(File.Exists(Path.Combine(r.Directory.Path, "bootstrap-result.json")));
    }

    [Theory]
    [InlineData("missing")] [InlineData("empty")] [InlineData("duplicate")] [InlineData("unknown")] [InlineData("nested")]
    [InlineData("version")] [InlineData("version-string")] [InlineData("pipe")] [InlineData("pipe-escape")]
    [InlineData("parent-zero")] [InlineData("parent-negative")] [InlineData("parent-overflow")] [InlineData("parent-float")]
    [InlineData("created-zero")] [InlineData("created-overflow")] [InlineData("created-string")] [InlineData("created-wrong")]
    [InlineData("timeout-low")] [InlineData("timeout-high")] [InlineData("timeout-bool")] [InlineData("oversize")]
    [InlineData("unicode")] [InlineData("trailing")] [InlineData("array")]
    public async Task InvalidBootstrapNeverReachesParentAuthorizationAndAlwaysClearsEnvironment(string defect)
    {
        await using var r = new Native(); var calls = 0;
        await using var pipe = new AsrockOpenRgbAuthorizationPipe((_, _) => calls++);
        var data = new JsonObject { ["version"] = 1, ["pipe"] = pipe.Name, ["parent"] = pipe.ParentId,
            ["created"] = pipe.ParentCreatedFileTime, ["timeoutMs"] = 1000 };
        string? raw = null;
        switch (defect)
        {
            case "missing": break;
            case "empty": raw = ""; break;
            case "duplicate": raw = "{\"version\":1," + data.ToJsonString()[1..]; break;
            case "unknown": data["extra"] = 1; break;
            case "nested": data["pipe"] = new JsonObject { ["name"] = pipe.Name }; break;
            case "version": data["version"] = 2; break;
            case "version-string": data["version"] = "1"; break;
            case "pipe": data["pipe"] = "other"; break;
            case "pipe-escape": data["pipe"] = pipe.Name + "\0"; break;
            case "parent-zero": data["parent"] = 0; break;
            case "parent-negative": data["parent"] = -1; break;
            case "parent-overflow": data["parent"] = (ulong)uint.MaxValue + 1; break;
            case "parent-float": data["parent"] = 1.5; break;
            case "created-zero": data["created"] = 0; break;
            case "created-overflow": raw = data.ToJsonString().Replace(pipe.ParentCreatedFileTime.ToString(), "18446744073709551616"); break;
            case "created-string": data["created"] = pipe.ParentCreatedFileTime.ToString(); break;
            case "created-wrong": data["created"] = pipe.ParentCreatedFileTime + 1; break;
            case "timeout-low": data["timeoutMs"] = 99; break;
            case "timeout-high": data["timeoutMs"] = 5001; break;
            case "timeout-bool": data["timeoutMs"] = true; break;
            case "oversize": raw = data.ToJsonString() + new string(' ', 1025); break;
            case "unicode": raw = data.ToJsonString() + "日本語"; break;
            case "trailing": raw = data.ToJsonString() + "{}"; break;
            case "array": raw = "[" + data.ToJsonString() + "]"; break;
        }
        if (defect != "missing") r.Start.Environment[AsrockOpenRgbStartupChannel.EnvironmentName] = raw ?? data.ToJsonString();
        var bound = new MockOpenRgbStartupChannel([]) { OnBind = owner =>
        {
            using var identity = new AsrockAuthorizationProcess(owner.ProcessId);
            pipe.Bind(identity.Id, identity.CreatedFileTime);
            _ = pipe.RunAsync().ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        } };
        await r.StartAsync(bound); Assert.Equal(new(21, 0, 0, true), await r.Read()); Assert.Equal(0, calls);
        await r.Owner!.DisposeAsync();
    }
}
