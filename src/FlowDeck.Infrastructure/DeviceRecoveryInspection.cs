using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

// Setup only reads this service-private tree. It never restores registers or removes an obligation.
internal static class DeviceRecoveryInspection
{
    internal static bool HasPending(string directory)
    {
        ServiceEnvironment.NoLinks(directory); if (!Directory.Exists(directory)) return false;
        var pending = false; var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (++count > 512) throw new IOException("汎用機器の復旧記録が上限を超えています");
            ServiceEnvironment.NoLinks(file); var name = Path.GetFileName(file);
            if (HashedName(name, "hub-endpoints-"))
            {
                var value = ServiceEnvironment.Read<HubEndpointRecoveryDocument>(file);
                if (value is not { Version: 2, Pending.Length: <= 5 } || value.Sha256 != SettingsTransfer.Hash(value.Pending))
                    throw new IOException("Hubのファン復旧記録を確認できません");
                pending |= value.Pending.Length != 0;
            }
            else if (HashedName(name, "hub-lighting-"))
            {
                var value = ServiceEnvironment.Read<HubLightingRecoveryDocument>(file);
                if (value is not { Version: 2, Pending.Length: <= 5 } || value.Sha256 != SettingsTransfer.Hash(value.Pending))
                    throw new IOException("Hubの照明復旧記録を確認できません");
                pending |= value.Pending.Length != 0;
            }
            // Successful ASRock/ENE recovery deletes these records. Any surviving record blocks replacement.
            else if (HashedName(name, "asrock-lighting-") || HashedName(name, "asrock-native-") || HashedName(name, "ene-lighting-") ||
                HashedName(name, AsrockOpenRgbDetectionRecovery.FilePrefix)) pending = true;
            else throw new IOException("未対応の汎用復旧記録があります: " + name);
        }
        foreach (var board in Directory.EnumerateDirectories(directory))
        {
            if (++count > 512) throw new IOException("汎用機器の復旧記録が上限を超えています");
            ServiceEnvironment.NoLinks(board);
            if (!HashedName(Path.GetFileName(board), "board-", "") || Directory.EnumerateDirectories(board).Any())
                throw new IOException("汎用復旧フォルダーの構成が不正です");
            foreach (var file in Directory.EnumerateFiles(board, "*.json"))
            {
                if (++count > 512 || Path.GetFileName(file) != "fan-recovery.json") throw new IOException("マザー復旧記録の構成が不正です");
                var value = ServiceEnvironment.Read<FanRecoveryDocument>(file);
                if (value is not { Version: 2, Binding: not null } || Path.GetFileName(board) != "board-" + SettingsTransfer.Hash(value.Binding.DeviceId))
                    throw new IOException("マザー復旧記録の関連付けが不正です");
                pending |= new FanRecoveryJournal(board, value.BootIdentity, value.Binding).Pending.Length != 0;
            }
        }
        return pending;
    }
    private static bool HashedName(string name, string prefix, string suffix = ".json") =>
        name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal) &&
        name.Length == prefix.Length + 64 + suffix.Length && name.AsSpan(prefix.Length, 64).ToArray().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
