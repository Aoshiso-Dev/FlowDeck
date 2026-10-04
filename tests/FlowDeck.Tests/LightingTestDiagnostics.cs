using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Xunit.Abstractions;

namespace FlowDeck.Tests;

internal static class LightingTestDiagnostics
{
    private static readonly JsonSerializerOptions Json = new(Contract.Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<DeviceOperationResult> ApplyAsync(ITestOutputHelper output, DeviceWorkspaceClient client, DeviceLightingEdit edit)
    {
        var snapshot = client.Snapshot!;
        var observed = snapshot.State.Endpoints.Where(e => e.Owned).Select(e => e.Target)
            .Concat(edit.Version.Targets.Select(t => t.Target)).ToHashSet();
        var threadsBefore = ThreadPool.ThreadCount;
        var pendingBefore = ThreadPool.PendingWorkItemCount;
        var began = Stopwatch.GetTimestamp();
        DeviceOperationResult? result = null;
        string? diagnostic = null;
        try { result = await client.ApplyLightingAsync(edit); }
        finally
        {
            // A failed start can restore its group before the reply arrives. Keep the operation's
            // write/persistence results separately from the returned state, without extra IPC or retries.
            diagnostic = JsonSerializer.Serialize(new {
                stage = edit.Operation == DeviceOperation.SetLighting ? "lighting-start" : "lighting-adjustment",
                effect = edit.Program?.EffectId, elapsedMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds,
                threadsBefore, pendingBefore, threadsAfter = ThreadPool.ThreadCount, pendingAfter = ThreadPool.PendingWorkItemCount,
                edit, result,
                before = new { snapshot.Configuration.Generation, snapshot.State.Inventory.TopologyRevision,
                    endpoints = snapshot.State.Endpoints.Where(e => observed.Contains(e.Target)).ToArray() },
                after = client.Snapshot is { } after ? new { after.Configuration.Generation, after.State.Inventory.TopologyRevision,
                    after.State.LifecycleError, endpoints = after.State.Endpoints.Where(e => observed.Contains(e.Target)).ToArray() } : null
            }, Json);
            output.WriteLine("LIGHTING_OPERATION " + diagnostic);
        }
        Assert.True(result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified, diagnostic);
        return result;
    }
}
