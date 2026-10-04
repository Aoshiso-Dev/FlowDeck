using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceConfigurationAuthority
{
    private readonly IDeviceOpenRgbInspector? openRgbInspector;
    private readonly SemaphoreSlim inspection = new(1, 1);
    public bool SupportsOpenRgbInspection => openRgbInspector is not null;
    private async Task<DeviceConfigurationReply> InspectOpenRgbAsync(DeviceConfigurationRequest request, CancellationToken ct)
    {
        if (openRgbInspector is null) throw new IOException("OpenRGBの接続確認に対応していません");
        if (!await inspection.WaitAsync(0, ct).ConfigureAwait(false)) throw new IOException("OpenRGBの接続確認中です");
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
            // Reading SDK settings must not block cooling-policy persistence or configuration transfers.
            var result = await openRgbInspector.InspectAsync(request.OpenRgbPort!.Value, ct).ConfigureAwait(false);
            await access.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Check();
                if (!DeviceOpenRgbInspection.Valid(result) || result.Port != request.OpenRgbPort)
                    throw new IOException("OpenRGBの確認結果が不正です");
                return new(Outcome.Confirmed, result.Message, generation.Id, OpenRgbInspection: Contract.Copy(result));
            }
            finally { access.Release(); }
        }
        finally { inspection.Release(); }
    }
}
