using System.Security.Cryptography;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceDiagnosticExport(DeviceDiagnosticReport Service, string? AppVersion, double TransferMilliseconds, string? AppBuildId = null);

public sealed partial class DeviceWorkspaceClient
{
    public bool SupportsDiagnostics => client.Hello?.Features.Contains(DeviceDiagnostics.Feature) == true;
    public async Task<DeviceDiagnosticExport> ReadDiagnosticsAsync(DeviceDiagnosticScope scope, CancellationToken ct = default)
    {
        if (!DeviceDiagnostics.ValidScope(scope)) throw new ArgumentException("診断の対象範囲が不正です");
        await operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            if (!SupportsDiagnostics) throw new IOException("診断に対応するサービスの更新が必要です");
            var includeHardware = client.Hello!.Features.Contains(DeviceDiagnosticHardwareRules.Feature);
            var start = clock.GetTimestamp(); var offset = 0; DeviceDiagnosticChunk? first = null;
            using var bytes = new MemoryStream();
            do
            {
                ct.ThrowIfCancellationRequested();
                // Cancellation between chunks is safe; never close a shared control pipe to cancel an already submitted request.
                var reply = await client.SendAsync(client.Request(DeviceOperation.ReadDiagnostics) with {
                    Diagnostics = new(scope, first?.TransferId, offset, includeHardware) }).ConfigureAwait(false);
                if (!reply.Success || reply.Diagnostics is not { } chunk || !DeviceDiagnostics.Valid(chunk))
                    throw new IOException("診断情報を取得できませんでした。もう一度作成してください");
                first ??= chunk;
                if (chunk.Scope != scope || chunk.Offset != offset || chunk.TransferId != first.TransferId || chunk.TotalBytes != first.TotalBytes || chunk.Sha256 != first.Sha256)
                    throw new IOException("診断情報の転送が一致しません");
                bytes.Write(chunk.Data); offset += chunk.Data.Length;
            } while (offset < first.TotalBytes);
            var data = bytes.ToArray();
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), first.Sha256, StringComparison.Ordinal))
                throw new IOException("診断情報の整合性を確認できません");
            var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(data, Contract.Json);
            if (report is null || report.FormatVersion != (includeHardware ? 2 : 1) || report.Scope != scope) throw new IOException("診断情報の形式が不正です");
            var app = DeviceDiagnostics.ApplicationInfo();
            return new(report, app?.Version, Math.Max(0, clock.GetElapsedTime(start).TotalMilliseconds), app?.BuildId);
        }
        catch (Exception e) { if (!client.Connected) RecordError(e); throw; }
        finally { operations.Release(); }
    }
}

public static class DeviceDiagnosticFiles
{
    public static async Task SaveAsync(string path, DeviceDiagnosticExport report, CancellationToken ct = default)
    {
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        catch (UnauthorizedAccessException e) { throw new IOException("診断情報を保存できません。保存先のアクセス権を確認してください", e); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
