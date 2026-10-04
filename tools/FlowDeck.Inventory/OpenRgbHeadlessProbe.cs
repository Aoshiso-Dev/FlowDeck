using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

// Explicit developer integration check of the pinned, complete OpenRGB executable.
// There is no selectable detection mode and no SDK mutation/device-info request here.
// Production startup still uses the protected launcher and its independent guards.
internal static class OpenRgbHeadlessProbe
{
    internal sealed record Observation(string Stage, int Controllers, uint ServerFlags, int Detectors, string SettingsDigest);
    internal sealed record Receipt(bool Passed, string RuntimeDefinition, string ArchiveSha256, uint ProcessId, int Port,
        bool DetectionRequested, bool ResourcesClosed, bool ProcessExited, bool GracefulExitConfirmed,
        bool ForcedTerminationRequired, Observation[] Observations, string? Error);

    public static async Task<Receipt> RunAsync(string runtimeDirectory, string outputDirectory)
    {
        var package = OpenRgbRuntimePackage.FlowDeckPinned;
        var runtime = package.Inspect(runtimeDirectory); // Reject arbitrary executables before creating output.
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("診断出力先は新しいフォルダーにしてください");
        Directory.CreateDirectory(outputDirectory);
        var config = Path.Combine(outputDirectory, "configuration");
        Directory.CreateDirectory(config);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, port);
        var settingsPath = Path.Combine(config, "OpenRGB.json");
        File.WriteAllBytes(settingsPath, profile.Settings);
        var start = profile.CreateStartInfo(runtimeDirectory, config);
        reservation.Stop(); // A collision is a failed probe, never permission to adopt another server.
        void Validate()
        {
            _ = package.Inspect(runtimeDirectory);
            if (!File.ReadAllBytes(settingsPath).AsSpan().SequenceEqual(profile.Settings) ||
                Directory.EnumerateFileSystemEntries(config).Count() != 1)
                throw new IOException("診断用の起動設定が変更されています");
        }

        var observations = new List<Observation>();
        OpenRgbOwnedProcess? owner = null;
        Process? process = null;
        uint pid = 0;
        Exception? failure = null;
        var passed = false;
        try
        {
            // User-writable output is intentional for this non-elevated developer probe only.
            // Exact package bytes remain leased until the owned child has actually exited.
            owner = await OpenRgbOwnedProcess.StartAsync(start, package.Acquire(runtimeDirectory, false), Validate,
                WindowsSuspendedProcess.Create, () => Task.CompletedTask,
                () => AsrockOpenRgbStartupChannel.Create(OpenRgbStartupKind.NoDetection, null), () => new OpenRgbShutdownSignal());
            pid = owner.ProcessId;
            process = Process.GetProcessById(checked((int)pid));
            _ = process.SafeHandle; // Retain the exact process object before the owner releases its handle.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (var connection = await Connect(owner, port, deadline.Token))
            {
                await Observe(connection, "initial", profile, observations, deadline.Token);
                await Observe(connection, "repeat", profile, observations, deadline.Token);
            }
            owner.CheckRunning();
            await using (var connection = await owner.ConnectAsync(port, deadline.Token))
                await Observe(connection, "reconnected", profile, observations, deadline.Token);
            if (observations.Select(o => o.SettingsDigest).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new IOException("再接続後のOpenRGB設定が一致しません");
            owner.CheckRunning();
            passed = true;
        }
        catch (Exception e) { failure = e; }
        finally
        {
            try { if (owner is not null) await owner.DisposeAsync(); }
            catch (Exception e) { failure = failure is null ? e : new AggregateException(failure, e); }
        }
        var closed = owner?.ResourcesClosed ?? false;
        var exited = process?.HasExited ?? false;
        process?.Dispose();
        var graceful = owner?.GracefulExitConfirmed ?? false;
        var forced = owner?.ForcedTerminationRequired ?? false;
        if (failure is null && (!closed || !exited || !graceful || forced))
            failure = new IOException("診断プロセスの正常終了を確認できません", owner?.GracefulShutdownFailure);
        var receipt = new Receipt(passed && failure is null, runtime.DefinitionId, runtime.ArchiveSha256, pid, port,
            false, closed, exited, graceful, forced, observations.ToArray(), failure?.ToString());
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        if (failure is not null) throw new IOException("OpenRGB本体の検出なし診断に失敗しました。result.jsonを確認してください", failure);
        return receipt;
    }

    private static async Task<OpenRgbSdkConnection> Connect(OpenRgbOwnedProcess owner, int port, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested(); owner.CheckRunning();
            try { return await owner.ConnectAsync(port, ct); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
            { await Task.Delay(50, ct); }
        }
    }

    private static async Task Observe(OpenRgbSdkConnection connection, string stage, OpenRgbStartupProfile profile,
        List<Observation> observations, CancellationToken ct)
    {
        var before = await connection.ReadEnvironmentAsync(ct);
        profile.CheckLiveEnvironment(before);
        var devices = await connection.ScanAsync(ct);
        if (devices.Length != 0) throw new IOException("検出なし起動で機器が公開されました");
        var after = await connection.ReadEnvironmentAsync(ct);
        profile.CheckLiveEnvironment(after);
        connection.CheckEnvironment(after.Stamp);
        if (before.Stamp.Digest != after.Stamp.Digest) throw new IOException("一覧取得の前後でOpenRGB設定が変化しました");
        observations.Add(new(stage, devices.Length, after.ServerFlags,
            after.Detectors.GetProperty("detectors").EnumerateObject().Count(), after.Stamp.Digest));
    }
}
