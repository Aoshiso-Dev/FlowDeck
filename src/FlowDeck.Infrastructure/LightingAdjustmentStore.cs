using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class LightingAdjustmentStore(string directory, string? groupId = null)
{
    public string PathName => Path.Combine(directory, groupId is null ? "lighting-adjustment.json" :
        Guid.TryParseExact(groupId, "N", out var id) && id.ToString("N") == groupId ? "lighting-adjustment-" + groupId + ".json" : throw new ArgumentException("グループ識別子が不正です"));
    public async Task<LightingAdjustmentState> LoadAsync()
    {
        if (!File.Exists(PathName)) return new();
        if (new FileInfo(PathName).Length > 1024 * 1024) throw new IOException("明るさの保存ファイルが大きすぎます");
        var value = JsonSerializer.Deserialize<LightingAdjustmentState>(await File.ReadAllTextAsync(PathName), Contract.Json);
        if (LightingBrightness.Validate(value) is { } invalid) throw new IOException(invalid);
        return value!;
    }
    public async Task SaveAsync(LightingAdjustmentState value)
    {
        if (LightingBrightness.Validate(value) is { } invalid) throw new ArgumentException(invalid);
        Directory.CreateDirectory(directory);
        var temporary = PathName + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await JsonSerializer.SerializeAsync(stream, value, Contract.Json); stream.Flush(true); }
        if (File.Exists(PathName)) File.Replace(temporary, PathName, PathName + ".backup"); else File.Move(temporary, PathName);
    }
}
