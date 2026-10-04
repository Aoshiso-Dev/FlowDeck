using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class HubWorkerClient(string pythonPath, string scriptPath, IEventLog log, Func<bool>? competingController = null, bool isolated = false) : IHubLightingEndpointAdapter
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private WorkerProcessCleanup? processCleanup;
    private Task? stdoutTask, stderrTask;
    private TaskCompletionSource<JsonElement>? greeting;
    private string? session;
    private long generation;
    private int stopping;
    public bool IsDemo => false;
    public bool SupportsFanEndpoints { get; private set; }
    public bool SupportsFanDeadlines { get; private set; }
    public bool SupportsLightingEndpoints { get; private set; }
    public bool SupportsLightingDeadlines { get; private set; }
    public bool SupportsSmoothLightingFrames { get; private set; }
    public string Version { get; private set; } = "未起動";
    private async Task StartAsync(CancellationToken ct)
    {
        var transport = HubDeviceDriver.DefinitionData.Transport;
        if (processCleanup is { ClosingStarted: false } && process is { HasExited: false } && session is not null) return;
        await TerminateAsync();
        if (!Path.IsPathFullyQualified(pythonPath) || !Path.IsPathFullyQualified(scriptPath) || !File.Exists(pythonPath) || !File.Exists(scriptPath))
            throw new FileNotFoundException("Python 3.13実行環境またはWorkerがありません。接続・設定で実行ファイルを選択してください");
        var start = new ProcessStartInfo(pythonPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (isolated)
        {
            start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.WorkingDirectory = Path.GetDirectoryName(scriptPath)!;
            start.Environment["PATH"] = Path.GetDirectoryName(pythonPath) + Path.PathSeparator + Environment.SystemDirectory;
        }
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(scriptPath);
        start.Environment["PYTHONUTF8"] = "1";
        greeting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process = Process.Start(start) ?? throw new IOException("Workerを起動できません");
        var own = process;
        processCleanup = new(new WorkerProcessLifetime(own));
        try
        {
            if (isolated) processCleanup.AttachJob(new OwnedProcessJob(own));
            stdoutTask = ReadOutputAsync(own);
            stderrTask = Task.Run(async () =>
            {
                var framer = new JsonLineFramer(65536); var bytes = new byte[4096];
                try { int count; while ((count = await own.StandardError.BaseStream.ReadAsync(bytes)) > 0)
                    foreach (var line in framer.Feed(bytes, count)) log.Write("worker", new { message = line }); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
            var ready = await greeting.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (!transport.AcceptsWorker(ready.GetProperty("v").GetInt32(), ready.GetProperty("pythonVersion").GetString(),
                ready.GetProperty("liquidctlVersion").GetString()))
                throw new IOException("検証対象はPython 3.13 / liquidctl 1.16.0です");
            session = ready.GetProperty("workerSessionId").GetString()!; generation = 0;
            var features = ready.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array
                ? capabilities.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).ToHashSet()
                : [];
            SupportsFanEndpoints = features.Contains("SetFanEndpoint");
            SupportsFanDeadlines = SupportsFanEndpoints && features.Contains("FanDeadlineTick64");
            SupportsLightingEndpoints = features.Contains("SetLightingEndpoints");
            SupportsLightingDeadlines = SupportsLightingEndpoints && features.Contains("LightingDeadlineTick64");
            SupportsSmoothLightingFrames = SupportsLightingDeadlines && features.Contains("LightingLatestStatusV1");
            Version = $"Python {ready.GetProperty("pythonVersion").GetString()} / liquidctl {transport.LiquidctlVersion} / protocol {transport.WorkerProtocol}";
        }
        catch (Exception original)
        {
            try { await TerminateAsync(); }
            catch (Exception closing) { throw new AggregateException("Workerの起動と後始末に失敗しました", original, closing); }
            throw;
        }
    }
    private async Task ReadOutputAsync(Process own)
    {
        var framer = new JsonLineFramer(65536); var bytes = new byte[4096];
        try
        {
            int count;
            while ((count = await own.StandardOutput.BaseStream.ReadAsync(bytes)) > 0)
            foreach (var line in framer.Feed(bytes, count))
            {
                try
                {
                    using var document = JsonDocument.Parse(line); var root = document.RootElement.Clone();
                    if (root.GetProperty("v").GetInt32() != 1) continue;
                    if (root.GetProperty("type").GetString() == "ready") greeting?.TrySetResult(root);
                    else if (root.GetProperty("type").GetString() == "response" && root.GetProperty("workerSessionId").GetString() == session &&
                        root.TryGetProperty("id", out var id) && pending.TryGetValue(id.GetString()!, out var completion)) completion.TrySetResult(root);
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
                { log.Write("workerProtocol", new { error = e.Message }); }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally
        {
            var error = new IOException("Workerが終了しました。未完了の結果は未確認です");
            greeting?.TrySetException(error);
            foreach (var completion in pending.Values) completion.TrySetException(error);
        }
    }
    private async Task<JsonElement> SendAsync(string command, object payload, CancellationToken ct, long? newGeneration = null, bool requiresDeadline = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref stopping) != 0, this);
        await serial.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref stopping) != 0, this);
            // Close never starts a replacement worker merely to close an already missing connection.
            if (command == "Close" && (processCleanup is not { ClosingStarted: false } || process is not { HasExited: false } || session is null)) return default;
            await StartAsync(ct);
            if (command == "SetFanEndpoint" && !SupportsFanEndpoints) throw new NotSupportedException("Workerは端子別ファン操作に対応していません");
            if (command == "SetLightingEndpoints" && !SupportsLightingEndpoints) throw new NotSupportedException("Workerは個体を照合する照明操作に対応していません");
            if (requiresDeadline && (command == "SetLightingEndpoints" ? !SupportsLightingDeadlines : !SupportsFanDeadlines))
                throw new NotSupportedException(command == "SetLightingEndpoints" ? "Workerは照明フレームの有効期限の確認に対応していません" : "Workerは温度の有効期限の確認に対応していません");
            var expectedGeneration = newGeneration ?? generation;
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                var line = JsonSerializer.Serialize(new { v = 1, type = "request", id, workerSessionId = session,
                    deviceGeneration = expectedGeneration, command, payload }, Contract.Json);
                if (Encoding.UTF8.GetByteCount(line) + 1 > 65536) throw new InvalidOperationException("Workerフレーム上限を超えました");
                await process!.StandardInput.WriteLineAsync(line.AsMemory(), ct); await process.StandardInput.FlushAsync(ct);
                var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(command == "OpenInitialize" ? 10 : command is "SetLightingEffect" or "SetLightingEndpoints" ? 1 : 5), ct);
                if (response.GetProperty("deviceGeneration").GetInt64() != expectedGeneration) throw new IOException("旧デバイス世代の応答を破棄しました");
                if (newGeneration is { } value) generation = value;
                return response;
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
            {
                var failure = new IOException(e is TimeoutException ? "WORKER_TIMEOUT: 結果未確認。自動再送しません" : "WORKER_DISCONNECTED: 結果未確認", e);
                try { await TerminateAsync(); }
                catch (Exception closing) { throw new AggregateException("Workerの通信と終了に失敗しました", failure, closing); }
                throw failure;
            }
            finally { pending.TryRemove(id, out _); }
        }
        finally { serial.Release(); }
    }
    private static HubStatus Status(JsonElement response)
    {
        if (!response.TryGetProperty("status", out var status) || status.ValueKind == JsonValueKind.Null)
            throw new IOException(response.TryGetProperty("error", out var e) ? e.GetString() : "状態を取得できません");
        return status.Deserialize<HubStatus>(Contract.Json) ?? throw new IOException("不正なHub状態");
    }
    public async Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default)
    {
        var response = await SendAsync("Probe", new { }, ct);
        return response.TryGetProperty("devices", out var devices) ? devices.Deserialize<HubIdentity[]>(Contract.Json)! : throw new IOException("対象を列挙できません");
    }
    public async Task<HubStatus> OpenAsync(HubIdentity identity, long generation, CancellationToken ct = default)
    {
        var camRunning = competingController?.Invoke() ?? CamRunning();
        if (camRunning) throw new InvalidOperationException("NZXT CAMが起動しています。対象Hubの二重制御を避けるため、CAMを終了してから接続してください");
        return Status(await SendAsync("OpenInitialize", new { identity }, ct, generation));
    }
    private static bool CamRunning()
    {
        var processes = Process.GetProcessesByName("NZXT CAM");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public async Task<HubStatus> ReadAsync(CancellationToken ct = default) => Status(await SendAsync("ReadStatus", new { }, ct));
    public async Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default)
        => Result(await SendAsync("SetFanGroup", new { channels, duty }, ct));
    public async Task<HubResult> SetFanEndpointAsync(string channel, int duty, HubEndpointExpectation expected, CancellationToken ct = default)
    {
        if (channel is not ("fan1" or "fan2" or "fan3" or "fan4" or "fan5") || duty is < 0 or > 100 || expected is null ||
            !DeviceConfigurationRules.Text(expected.HubId, 2048) || !DeviceConfigurationRules.Text(expected.WiringSignature, 4096) ||
            expected.CurrentDuty is < 0 or > 100 || expected.ValidUntilTick64 is <= 0)
            throw new ArgumentException("端子別ファン操作の条件が不正です");
        if (expected.ValidUntilTick64 is { } deadline)
            return Result(await SendAsync("SetFanEndpoint", new { channel, duty, expectedHubId = expected.HubId,
                expectedWiringSignature = expected.WiringSignature, expectedCurrentDuty = expected.CurrentDuty, validUntilTick64 = deadline }, ct, requiresDeadline: true));
        return Result(await SendAsync("SetFanEndpoint", new { channel, duty, expectedHubId = expected.HubId,
            expectedWiringSignature = expected.WiringSignature, expectedCurrentDuty = expected.CurrentDuty }, ct));
    }
    public async Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default)
        => Result(await SendAsync("SetLighting", new { channels, preset }, ct));
    public async Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default)
        => Result(await SendAsync("SetLightingEffect", new { channels, mode = effect.Mode,
            colors = effect.Colors.Select(c => new[] { c.R, c.G, c.B }), speed = effect.Speed, reverse = effect.Reverse }, ct));
    public async Task<HubResult> SetLightingEndpointsAsync(string[] channels, HubLightSetting effect,
        HubLightingExpectation expected, CancellationToken ct = default)
    {
        if (channels is null || channels.Length is < 1 or > 5 ||
            channels.Any(c => c is not ("led1" or "led2" or "led3" or "led4" or "led5")) || channels.Distinct().Count() != channels.Length ||
            !HubLighting.Valid(effect) || expected is null || !DeviceConfigurationRules.Text(expected.HubId, 2048) ||
            !DeviceConfigurationRules.Text(expected.WiringSignature, 4096) || expected.ValidUntilTick64 is <= 0)
            throw new ArgumentException("端子別照明操作の条件が不正です");
        var targets = channels.ToArray(); var colors = effect.Colors.Select(c => new[] { c.R, c.G, c.B }).ToArray();
        if (expected.ValidUntilTick64 is { } deadline)
            return Result(await SendAsync("SetLightingEndpoints", new { channels = targets, mode = effect.Mode, colors,
                speed = effect.Speed, reverse = effect.Reverse, expectedHubId = expected.HubId,
                expectedWiringSignature = expected.WiringSignature, validUntilTick64 = deadline }, ct, requiresDeadline: true));
        return Result(await SendAsync("SetLightingEndpoints", new { channels = targets, mode = effect.Mode, colors,
            speed = effect.Speed, reverse = effect.Reverse, expectedHubId = expected.HubId, expectedWiringSignature = expected.WiringSignature }, ct));
    }
    private static HubResult Result(JsonElement response) => new(response.GetProperty("outcome").Deserialize<Outcome>(Contract.Json),
        response.TryGetProperty("status", out var data) && data.ValueKind != JsonValueKind.Null ? data.Deserialize<HubStatus>(Contract.Json) : null,
        response.TryGetProperty("channels", out var channels) ? channels.Deserialize<ChannelResult[]>(Contract.Json) : null,
        response.TryGetProperty("error", out var error) ? error.GetString() : null);
    public async Task CloseAsync(CancellationToken ct = default) => _ = await SendAsync("Close", new { }, ct);
    private async Task TerminateAsync()
    {
        if (processCleanup is { } cleanup)
        {
            session = null;
            SupportsFanEndpoints = SupportsFanDeadlines = SupportsLightingEndpoints = SupportsLightingDeadlines = SupportsSmoothLightingFrames = false;
            Version = "終了待ち";
            try
            {
                await cleanup.CloseAsync(new[] { stdoutTask, stderrTask }.OfType<Task>()).ConfigureAwait(false);
            }
            finally
            {
                if (cleanup.ResourcesClosed)
                {
                    process = null; processCleanup = null; stdoutTask = stderrTask = null; greeting = null;
                    Version = "停止";
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        await serial.WaitAsync(); try { await TerminateAsync(); } finally { serial.Release(); }
    }
}
