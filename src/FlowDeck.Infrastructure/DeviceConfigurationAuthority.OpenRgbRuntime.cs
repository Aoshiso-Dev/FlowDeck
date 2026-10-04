using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private readonly IDeviceOpenRgbRuntimeInspector? openRgbRuntimeInspector;
    private readonly SemaphoreSlim runtimeInspection = new(1,1);
    public bool SupportsOpenRgbRuntimeInspection => openRgbRuntimeInspector is not null;

    private async Task<DeviceConfigurationReply> InspectOpenRgbRuntimeAsync(DeviceConfigurationRequest request, CancellationToken ct)
    {
        if (openRgbRuntimeInspector is null) throw new IOException("OpenRGBの導入確認に対応していません");
        if (!await runtimeInspection.WaitAsync(0,ct).ConfigureAwait(false)) throw new IOException("OpenRGBの導入状態を確認中です");
        try
        {
            void Check()
            {
                ct.ThrowIfCancellationRequested(); CheckCurrent();
                if (applyingConfiguration || request.ExpectedGeneration != generation.Id)
                    throw new IOException("設定が変更されました。現在の設定で再確認してください");
            }
            await access.WaitAsync(ct).ConfigureAwait(false);
            try { Check(); } finally { access.Release(); }
            // Hashing optional files must not serialize cooling-policy persistence or live configuration edits.
            var result = await openRgbRuntimeInspector.InspectAsync(ct).ConfigureAwait(false);
            await access.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Check();
                if (!DeviceOpenRgbRuntimeInspection.Valid(result)) throw new IOException("OpenRGBの導入確認結果が不正です");
                return new(Outcome.Confirmed,"OpenRGBの導入状態を確認しました",generation.Id,OpenRgbRuntime:result with { });
            }
            finally { access.Release(); }
        }
        finally { runtimeInspection.Release(); }
    }
}
