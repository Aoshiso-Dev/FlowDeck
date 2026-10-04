using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceSettingsArchive(string Format, int Version, string Purpose, DateTimeOffset CreatedUtc,
    DeviceConfigurationPackage Configuration, string Sha256);

/// <summary>A bounded v2-only archive of typed settings. No recovery journal, credentials, logs or runtime ownership.</summary>
public static class DeviceSettingsArchives
{
    public const string Format = "flowdeck-devices-v2";
    public const int MaximumBytes = DeviceConfigurationProtocol.MaximumBytes + 4096;
    private static string Hash(DeviceSettingsArchive value) => SettingsTransfer.Hash(new { value.Format,value.Version,value.Purpose,value.CreatedUtc,value.Configuration });
    public static DeviceSettingsArchive Create(DeviceConfigurationPackage source, bool portable, UiSettings? appearance = null)
    {
        DeviceConfigurationCodec.Validate(source);
        var config=Contract.Copy(source);
        if(appearance is not null) config=config with { Preferences=config.Preferences with { Ui=appearance } };
        config=config with { Migration=new(2,"native-v2",SettingsTransfer.Hash(config.Devices),[],[]) };
        if(portable) config=DeviceConfigurationCodec.Portable(config);
        _=DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(config));
        var archive=new DeviceSettingsArchive(Format,1,portable?"portable":"backup",DateTimeOffset.UtcNow,config,"");
        return archive with { Sha256=Hash(archive) };
    }
    public static byte[] Write(DeviceSettingsArchive archive)
    {
        Validate(archive);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(archive,SettingsTransfer.Indented);
        if(bytes.Length>MaximumBytes) throw new IOException("書き出す設定が大きすぎます");
        return bytes;
    }
    public static DeviceSettingsArchive Read(byte[] bytes)
    {
        if(bytes.Length is < 2 or > MaximumBytes) throw new IOException("設定ファイルの長さが不正です");
        try
        {
            using var json=JsonDocument.Parse(bytes); SettingsTransfer.NoDuplicates(json.RootElement);
            var archive=json.RootElement.Deserialize<DeviceSettingsArchive>(Contract.Json)??throw new IOException("設定ファイルが空です");
            Validate(archive); return archive;
        }
        catch(Exception e) when(e is JsonException or ArgumentException or NullReferenceException)
        { throw new IOException("FlowDeck汎用版の設定ファイルを選択してください",e); }
    }
    private static void Validate(DeviceSettingsArchive archive)
    {
        if(archive is not { Format:Format,Version:1,Purpose:"backup" or "portable",Configuration:not null } ||
            archive.CreatedUtc==default || !DeviceConfigurationProtocol.Digest(archive.Sha256)) throw new IOException("未対応の設定ファイルです");
        _=DeviceConfigurationCodec.Read(DeviceConfigurationCodec.Write(archive.Configuration));
        if(archive.Sha256!=Hash(archive)) throw new IOException("設定ファイルの整合性を確認できません");
        if(archive.Configuration.Migration.References.Length!=0 || archive.Configuration.Migration.Notices.Length!=0 ||
            archive.Configuration.Migration.SourceFormat!=(archive.Purpose=="portable"?"portable-v2":"native-v2"))
            throw new IOException("この形式では旧設定の変換記録を取り込めません");
        if(archive.Purpose=="portable")
        {
            var normalized=DeviceConfigurationCodec.Portable(archive.Configuration) with { Migration=archive.Configuration.Migration };
            if(SettingsTransfer.Hash(normalized)!=SettingsTransfer.Hash(archive.Configuration) || archive.Configuration.Migration is not { SourceFormat:"portable-v2",References.Length:0,Notices.Length:0 })
                throw new IOException("持ち運び用設定に個体の確認情報が含まれています");
        }
    }
    public static async Task<DeviceSettingsArchive> ReadAsync(string path,CancellationToken ct=default)
    {
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,8192,true);
        if(file.Length>MaximumBytes) throw new IOException("設定ファイルが大きすぎます");
        using var buffer=new MemoryStream(); var chunk=new byte[8192]; int count;
        while((count=await file.ReadAsync(chunk,ct).ConfigureAwait(false))!=0)
        { if(buffer.Length+count>MaximumBytes) throw new IOException("設定ファイルが大きすぎます"); await buffer.WriteAsync(chunk.AsMemory(0,count),ct).ConfigureAwait(false); }
        return Read(buffer.ToArray());
    }
    public static async Task SaveAsync(string path,DeviceSettingsArchive archive,CancellationToken ct=default)
    {
        if(!path.EndsWith(".flowdeck.json",StringComparison.OrdinalIgnoreCase)) throw new IOException("書き出し先の拡張子は .flowdeck.json にしてください");
        var bytes=Write(archive); var full=Path.GetFullPath(path); var parent=Path.GetDirectoryName(full)!;
        if(!Directory.Exists(parent)) throw new IOException("書き出し先のフォルダーがありません");
        if(new FileInfo(full).LinkTarget is not null) throw new IOException("リンク先へ設定を書き出せません");
        var temporary=Path.Combine(parent,".flowdeck-export-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes,ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary,full,true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}
