using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// A reference to a protected SDK journal, never a caller-supplied filesystem path. The native
// owner records it while the SDK journal's read lease is held and before any SDK mutation.
internal sealed record OpenRgbSdkRecoveryLink(OpenRgbRecoveryScope Scope, string Boot, string Revision,
    string Hash, string? PreviousHash, OpenRgbRecoveryPhase Phase)
{
    internal static bool Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal bool Valid => Scope is { Binding: not null, Target: not null } &&
        DeviceConfigurationRules.Key(Scope.Binding.DeviceId) && DeviceConfigurationRules.Key(Scope.Binding.ProviderIdentity) &&
        DeviceConfigurationRules.Key(Scope.Binding.DefinitionId) && Hex(Scope.Binding.Fingerprint) &&
        Scope.Target.Connection != Guid.Empty && Scope.Target.InventoryRevision > 0 && Hex(Scope.Target.Structure) &&
        Scope.Zone is null or >= 0 and < 8 && Enum.IsDefined(Scope.Confidence) && DeviceServiceProtocol.Id(Scope.Owner) &&
        DeviceConfigurationRules.Text(Boot, 128) && DeviceServiceProtocol.Id(Revision) && Hex(Hash) &&
        (PreviousHash is null || Hex(PreviousHash)) && Enum.IsDefined(Phase);
    internal static OpenRgbSdkRecoveryLink From(OpenRgbRecoveryRecord record) => new(Contract.Copy(record.Scope), record.Boot,
        record.Revision, record.Hash, record.PreviousHash, record.Phase);
    internal bool SameSlot(OpenRgbSdkRecoveryLink other) => Scope.Binding.DeviceId == other.Scope.Binding.DeviceId && Scope.Zone == other.Scope.Zone;
    internal bool Matches(OpenRgbRecoveryRecord record) => Valid && record.Scope == Scope && record.Boot == Boot &&
        (record.Hash == Hash || Phase == OpenRgbRecoveryPhase.Active && record.Hash == PreviousHash ||
         record.PreviousHash == Hash && record.Phase is OpenRgbRecoveryPhase.Recovering or OpenRgbRecoveryPhase.Accepted);
}

internal sealed partial class OpenRgbLightingRecovery
{
    internal void CheckAbsent()
    {
        lock (gate)
        {
            ServiceEnvironment.NoLinks(path);
            if (File.Exists(path)) throw new IOException("OpenRGBの復旧記録が残っています");
        }
    }
    internal void ForgetVerifiedNative(Action checkNativeComplete)
    {
        lock (gate) { checkNativeComplete(); CheckAbsent(); pending = null; }
    }

    internal void SettleNative(OpenRgbSdkRecoveryLink link, Action checkVerifiedNative)
    {
        lock (gate)
        {
            checkVerifiedNative();
            if (!link.Valid || link.Scope.Binding.DeviceId != deviceId || link.Scope.Zone != zone)
                throw new IOException("Native復旧とSDK記録の対象が一致しません");
            if (pending is null)
            {
                if (File.Exists(path)) throw new IOException("SDK復旧記録が後から作成されました");
                return;
            }
            CheckCurrent(pending);
            if (!link.Matches(pending)) throw new IOException("SDK復旧記録が保存済みのNative操作と一致しません");
            checkVerifiedNative(); Store(null);
        }
    }
}
