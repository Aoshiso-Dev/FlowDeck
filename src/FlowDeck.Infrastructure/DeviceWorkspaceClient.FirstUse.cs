using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceFirstUseResult(int PreparedLighting, string? Deferred = null);

public sealed partial class DeviceWorkspaceClient
{
    private int firstUseAttempted;

    /// <summary>Normal launch only. Prepare known, never-configured lighting; never start an output or guess cooling wiring.</summary>
    public async Task<DeviceFirstUseResult> PrepareFirstUseAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref firstUseAttempted, 1) != 0 || !SupportsEndpointSetup) return new(0);
        var prepared = 0;
        try
        {
            await RefreshAsync(true, ct).ConfigureAwait(false);
            var initial = Snapshot!;
            var epoch = initial.State.Inventory.Epoch;
            // A stable physical identity and a compiled driver's detected LED count are both required.
            // Durable setup still excludes mismatches, revoked setup and connection-only identities.
            // ENE connection-only preparation is a separate service-owned, non-persistent operation below.
            var targets = DeviceDetectedLightingSetup.AutomaticTargets(initial);
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                await RefreshAsync(true, ct).ConfigureAwait(false);
                var current = Snapshot!;
                if (current.State.Inventory.Epoch != epoch || current.ExitAttempted) break;
                var originalBinding = initial.Configuration.Candidates.Single(c => c.DeviceId == target.DeviceId);
                if (!current.Configuration.Candidates.Contains(originalBinding)) continue;
                var first = initial.Configuration.Setup!.Single(c => c.Target == target);
                var now = current.Configuration.Setup?.SingleOrDefault(c => c.Target == target);
                if (now is null || !System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(first.Options, Contract.Json).AsSpan()
                        .SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(now.Options, Contract.Json))) continue;
                if (DeviceDetectedLightingSetup.Create(current, target) is not { } draft) continue;
                var preview = await PreviewSetupAsync(draft.Capture(), ct).ConfigureAwait(false);
                var result = await ApplySetupAsync(preview, ct).ConfigureAwait(false);
                if (result.Outcome != Outcome.Confirmed) return new(prepared, result.Message);
                prepared++;
            }
            prepared += await EnableConnectionLightingAsync(ct).ConfigureAwait(false);
            return new(prepared, connectionLightingDeferred);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // A failed preparation never prevents opening the monitor and is not replayed by polling/reconnect.
            return new(prepared, e.Message);
        }
    }
}
