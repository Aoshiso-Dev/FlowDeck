namespace FlowDeck.Infrastructure;

/// <summary>Local binding scope, deliberately separate from portable settings and recovery journals.</summary>
public static class DeviceInstallationIdentity
{
    public static string LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "device-installation-id");
        string Read()
        {
            if (new FileInfo(path).Length != 32) throw new IOException("機器登録の識別情報が不正です");
            var id = File.ReadAllText(path);
            return Guid.TryParseExact(id, "N", out _) ? id : throw new IOException("機器登録の識別情報が不正です");
        }
        if (File.Exists(path)) return Read();
        var candidate = Guid.NewGuid().ToString("N"); var temporary = path + "." + candidate + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(System.Text.Encoding.ASCII.GetBytes(candidate)); stream.Flush(true); }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { return Read(); }
            return candidate;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
