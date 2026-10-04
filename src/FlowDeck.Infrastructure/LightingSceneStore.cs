using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class LightingSceneStore(string directory)
{
    private readonly SemaphoreSlim gate = new(1);
    public string PathName => Path.Combine(directory, "lighting-scenes.json");
    public async Task<LightingSceneCollection> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(PathName)) return new(Scenes: []);
            var info = new FileInfo(PathName); if (info.Length > 1024 * 1024) throw new IOException("シーンファイルが大きすぎます");
            var value = JsonSerializer.Deserialize<LightingSceneCollection>(await File.ReadAllTextAsync(PathName), Contract.Json);
            if (LightingScenes.Validate(value) is { } error) throw new IOException(error);
            return value!;
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(LightingSceneCollection value)
    {
        if (LightingScenes.Validate(value) is { } error) throw new ArgumentException(error);
        var snapshot = Contract.Copy(value);
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = PathName + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(stream, snapshot, Contract.Json); stream.Flush(true); }
            if (File.Exists(PathName)) File.Replace(temporary, PathName, PathName + ".backup"); else File.Move(temporary, PathName);
        }
        finally { gate.Release(); }
    }
}
