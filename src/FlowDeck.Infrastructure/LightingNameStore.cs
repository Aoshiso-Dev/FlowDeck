using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class LightingNameStore(string directory)
{
    public string PathName => Path.Combine(directory, "lighting-names.json");
    public async Task<LightingNameCollection> LoadAsync()
    {
        if (!File.Exists(PathName)) return new(Names: []);
        if (new FileInfo(PathName).Length > 1024 * 1024) throw new IOException("照明名のファイルが大きすぎます");
        var value = JsonSerializer.Deserialize<LightingNameCollection>(await File.ReadAllTextAsync(PathName), Contract.Json);
        if (LightingNames.Validate(value) is { } error) throw new IOException(error);
        return value!;
    }
    public async Task SaveAsync(LightingNameCollection value)
    {
        if (LightingNames.Validate(value) is { } error) throw new ArgumentException(error);
        var snapshot = Contract.Copy(value); Directory.CreateDirectory(directory);
        var temporary = PathName + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await JsonSerializer.SerializeAsync(stream, snapshot, Contract.Json); stream.Flush(true); }
        if (File.Exists(PathName)) File.Replace(temporary, PathName, PathName + ".backup"); else File.Move(temporary, PathName);
    }
}
