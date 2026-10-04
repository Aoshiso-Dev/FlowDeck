using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

[Collection("Wall-clock transport")]
public sealed class AsrockOpenRgbAuthorizationPipeTests
{
    private sealed class Native : IDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public Process Process { get; } = null!;
        private AsrockAuthorizationProcess? identity;
        public AsrockAuthorizationProcess Identity => identity ??= new((uint)Process.Id);
        private readonly Task<string> output;
        public Native(string name, uint parent, ulong created, string mode = "trace", int timeout = 1500)
        {
            try
            {
                var exe = AsrockAuthorizationFixture.Extract(directory.Path);
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.Path };
                foreach (var arg in new[] { name, parent.ToString(), created.ToString(), mode, timeout.ToString() }) start.ArgumentList.Add(arg);
                Process = System.Diagnostics.Process.Start(start)!; output = Process.StandardOutput.ReadToEndAsync();
            }
            catch
            {
                if (Process is not null) { if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(5000); } Process.Dispose(); }
                directory.Dispose(); throw;
            }
        }
        public async Task<(int Code, string Text)> Exit()
        {
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            return (Process.ExitCode, await output);
        }
        public void Dispose()
        {
            identity?.Dispose();
            if (!Process.HasExited)
            {
                Process.Kill(true);
                if (!Process.WaitForExit(5000)) throw new IOException("Native試験プロセスの終了が未確認です");
            }
            Process.Dispose(); AsrockAuthorizationFixture.DeleteExitedImage(directory.Path); directory.Dispose();
        }
    }
    private static async Task End(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(8)); } catch (Exception e) when (e is not TimeoutException) { } }
    [Fact] public async Task RealNativeProcessUsesAllBoundariesAndCarriesSdkOrigin()
    {
        var received = new List<AsrockAuthorizationRequest>();
        await using var server = new AsrockOpenRgbAuthorizationPipe((r, _) => received.Add(r));
        using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime);
        server.Bind(client.Identity.Id, client.Identity.CreatedFileTime); var run = server.RunAsync();
        var result = await client.Exit(); await End(run);
        Assert.Equal(0, result.Code); Assert.Contains("GRANTED writes=2 reads=2", result.Text);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (ulong)i), received.Select(r => r.Sequence));
        Assert.Equal(new[] { 0, 2, 3, 4, 1, 2, 3, 4 }, received.Select(r => (int)r.Phase));
        Assert.All(received.Take(5), r => Assert.False(r.Origin.Sdk));
        Assert.All(received.Skip(5), r => Assert.Equal(new(true, 6, 1050, 42, 6742, 51000), r.Origin));
        Assert.All(received, r => Assert.Equal(@"\\?\hid#vid_26ce&pid_01a2#pipe-fixture", r.Path));
        await server.DisposeAsync(); Assert.True(server.ResourcesClosed);
    }
    [Theory]
    [InlineData(1,0,0)] [InlineData(2,0,0)] [InlineData(3,1,0)] [InlineData(4,1,1)]
    [InlineData(5,1,1)] [InlineData(6,1,1)] [InlineData(7,2,1)] [InlineData(8,2,2)]
    public async Task DenialStopsAtTheExactNativeBoundaryAndCannotReconnect(int deny, int writes, int reads)
    {
        var calls = 0;
        await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => { if (++calls == deny) throw new IOException("revoked"); });
        using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime);
        server.Bind(client.Identity.Id, client.Identity.CreatedFileTime); var run = server.RunAsync();
        var result = await client.Exit(); await End(run);
        Assert.Equal(20, result.Code); Assert.Equal(deny, calls);
        Assert.Contains($"DENIED writes={writes} reads={reads} reused=0 faulted=1", result.Text);
        Assert.NotNull(server.Failure); Assert.Throws<InvalidOperationException>(() => { _ = server.RunAsync(); });
    }
    [Theory] [InlineData("raw-sequence",0)] [InlineData("raw-replay",1)] [InlineData("raw-duplicate",0)]
    [InlineData("raw-large",0)] [InlineData("raw-zero",0)] [InlineData("raw-partial",0)]
    public async Task InvalidFramesNeverReachAuthorization(string mode, int permitted)
    {
        var calls = 0;
        await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => calls++, TimeSpan.FromMilliseconds(200));
        using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime, mode);
        server.Bind(client.Identity.Id, client.Identity.CreatedFileTime); var run = server.RunAsync();
        var result = await client.Exit(); await End(run);
        Assert.Equal(22, result.Code); Assert.Equal(permitted, calls); Assert.NotNull(server.Failure);
    }
    [Fact] public async Task LateCallbackCannotPublishPermission()
    {
        var calls = 0;
        await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => { calls++; Thread.Sleep(400); }, TimeSpan.FromMilliseconds(100));
        using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime, timeout: 200);
        server.Bind(client.Identity.Id, client.Identity.CreatedFileTime); var run = server.RunAsync();
        var result = await client.Exit(); await End(run);
        Assert.Equal(20, result.Code); Assert.Equal(1, calls); Assert.Contains("writes=0 reads=0 reused=0", result.Text);
    }
    [Fact] public async Task DisposingAnActiveChannelUnblocksTheNativeClient()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new AsrockOpenRgbAuthorizationPipe((_, ct) => { entered.SetResult(); ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); });
        using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime);
        server.Bind(client.Identity.Id, client.Identity.CreatedFileTime); var run = server.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
        var result = await client.Exit(); await End(run);
        Assert.Equal(20, result.Code); Assert.Contains("writes=0 reads=0", result.Text); Assert.True(server.ResourcesClosed);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeClientRejectsWrongParentCreationOrServerPid(bool wrongPid)
    {
        await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => throw new Xunit.Sdk.XunitException("must not authorize"));
        using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime + (wrongPid ? 0UL : 1UL), wrongPid ? "self-parent" : "trace");
        var result = await client.Exit(); Assert.Equal(21, result.Code); Assert.Contains("CONNECT_DENIED", result.Text);
    }
    [Fact] public async Task ServerRejectsAnotherLiveProcessBeforeReadingRequests()
    {
        using var directory = new ServiceTestDirectory();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")) { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory.Path };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit 0");
        var other = WindowsSuspendedProcess.Create(start); using var job = other.AttachJob();
        try
        {
            using var identity = new AsrockAuthorizationProcess(other.ProcessId); var calls = 0;
            await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => calls++);
            server.Bind(identity.Id, identity.CreatedFileTime);
            using var client = new Native(server.Name, server.ParentId, server.ParentCreatedFileTime);
            var run = server.RunAsync(); var result = await client.Exit(); await End(run);
            Assert.Equal(20, result.Code); Assert.Equal(0, calls); Assert.IsType<UnauthorizedAccessException>(server.Failure);
        }
        finally { other.Kill(); other.Dispose(); }
    }
    [Theory]
    [InlineData("denied")] [InlineData("sequence")] [InlineData("duplicate")] [InlineData("version")] [InlineData("allow-type")]
    [InlineData("nested")] [InlineData("extra")] [InlineData("large")] [InlineData("partial")] [InlineData("timeout")] [InlineData("fragmented")]
    public async Task NativeReplyParserIsBoundedStrictAndHandlesPartialReads(string kind)
    {
        using var parent = new AsrockAuthorizationProcess((uint)Environment.ProcessId);
        var name = AsrockOpenRgbAuthorizationProtocol.PipePrefix + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new Native(name, parent.Id, parent.CreatedFileTime, timeout: kind == "timeout" ? 200 : 1500);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await pipe.WaitForConnectionAsync(deadline.Token);
        for (var i = 1; i <= (kind == "fragmented" ? 8 : 1); i++)
        {
            var header = new byte[4]; await pipe.ReadExactlyAsync(header, deadline.Token);
            var body = new byte[AsrockOpenRgbAuthorizationProtocol.Length(header, 16384)]; await pipe.ReadExactlyAsync(body, deadline.Token);
            var request = AsrockOpenRgbAuthorizationProtocol.Read(body); Assert.Equal((ulong)i, request.Sequence);
            if (kind == "timeout") { await Task.Delay(350, deadline.Token); break; }
            var reply = kind switch
            {
                "denied" => $"{{\"version\":1,\"sequence\":{i},\"allow\":false}}",
                "sequence" => "{\"version\":1,\"sequence\":2,\"allow\":true}",
                "duplicate" => "{\"version\":1,\"sequence\":1,\"allow\":false,\"allow\":true}",
                "version" => "{\"version\":2,\"sequence\":1,\"allow\":true}",
                "allow-type" => "{\"version\":1,\"sequence\":1,\"allow\":1}",
                "nested" => "{\"version\":1,\"sequence\":1,\"allow\":{\"allow\":true}}",
                "extra" => "{\"version\":1,\"sequence\":1,\"allow\":true,\"extra\":1}",
                _ => $"{{\"version\":1,\"sequence\":{i},\"allow\":true}}"
            };
            var bytes = Encoding.UTF8.GetBytes(reply); BinaryPrimitives.WriteInt32LittleEndian(header,kind == "large" ? 129 : bytes.Length);
            if (kind == "fragmented")
            {
                foreach (var b in header.Concat(bytes)) await pipe.WriteAsync(new byte[] { b }, deadline.Token);
            }
            else
            {
                await pipe.WriteAsync(header, deadline.Token);
                if (kind != "large") await pipe.WriteAsync(kind == "partial" ? bytes[..3] : bytes, deadline.Token);
            }
            if (kind == "partial") { pipe.Dispose(); break; }
        }
        var result = await client.Exit(); Assert.Equal(kind == "fragmented" ? 0 : 20, result.Code);
        if (kind != "fragmented") Assert.Contains("writes=0 reads=0 reused=0", result.Text);
    }

    [Fact] public async Task ChildExitBeforeConnectingCancelsTheChannelPromptly()
    {
        using var directory = new ServiceTestDirectory();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")) { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory.Path };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit 0");
        var child = WindowsSuspendedProcess.Create(start); using var job = child.AttachJob();
        try
        {
            using var identity = new AsrockAuthorizationProcess(child.ProcessId);
            await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => throw new Xunit.Sdk.XunitException("no client exists"));
            server.Bind(identity.Id, identity.CreatedFileTime); var run = server.RunAsync(); child.Resume();
            Assert.True(await child.WaitForExitAsync(TimeSpan.FromSeconds(3)));
            await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(run.IsCompleted); Assert.NotNull(server.Failure);
            await server.DisposeAsync(); Assert.True(server.ResourcesClosed);
        }
        finally { child.Kill(); child.Dispose(); }
    }

    [Fact] public async Task ServerBindingRequiresAnExactOtherProcessAndIsImmutable()
    {
        using var directory = new ServiceTestDirectory();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")) { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory.Path };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit 0");
        var child = WindowsSuspendedProcess.Create(start); using var job = child.AttachJob();
        try
        {
            using var identity = new AsrockAuthorizationProcess(child.ProcessId);
            await using var server = new AsrockOpenRgbAuthorizationPipe((_, _) => { });
            Assert.Throws<InvalidOperationException>(() => { _ = server.RunAsync(); });
            Assert.Throws<InvalidOperationException>(() => server.Bind(server.ParentId, server.ParentCreatedFileTime));
            Assert.Throws<UnauthorizedAccessException>(() => server.Bind(identity.Id, identity.CreatedFileTime+1));
            server.Bind(identity.Id, identity.CreatedFileTime);
            Assert.Throws<InvalidOperationException>(() => server.Bind(identity.Id, identity.CreatedFileTime));
            await server.DisposeAsync(); Assert.True(server.ResourcesClosed);
            Assert.Throws<InvalidOperationException>(() => server.Bind(identity.Id, identity.CreatedFileTime));
        }
        finally { child.Kill(); child.Dispose(); }
    }
}
