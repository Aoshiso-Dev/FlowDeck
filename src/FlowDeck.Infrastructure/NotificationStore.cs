using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed class NotificationStore(string directory)
{
    public string PathName => Path.Combine(directory,"notifications.json");
    public async Task<NotificationSettings> LoadAsync()
    {
        if (!File.Exists(PathName)) return new(Rules: []);
        if (new FileInfo(PathName).Length > 1024 * 1024) throw new IOException("通知設定ファイルが大きすぎます");
        var value = JsonSerializer.Deserialize<NotificationSettings>(await File.ReadAllTextAsync(PathName),Contract.Json);
        if (Notifications.Validate(value) is { } error) throw new IOException(error);
        return value!;
    }
    public async Task SaveAsync(NotificationSettings value)
    {
        if (Notifications.Validate(value) is { } error) throw new ArgumentException(error);
        var snapshot = Contract.Copy(value); Directory.CreateDirectory(directory); var temporary = PathName + ".tmp";
        await using (var stream = new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
        { await JsonSerializer.SerializeAsync(stream,snapshot,Contract.Json); stream.Flush(true); }
        if (File.Exists(PathName)) File.Replace(temporary,PathName,PathName + ".backup"); else File.Move(temporary,PathName);
    }
}
