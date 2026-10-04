namespace FlowDeck.Infrastructure;

/// <summary>The installed generic GUI never reads legacy settings or selects a direct hardware host.</summary>
public sealed record DeviceStartupOptions(string Directory, bool StartInTray, bool Smoke)
{
    public static string DefaultDirectory(string localAppData) => Path.Combine(Path.GetFullPath(localAppData), "FlowDeck", "devices-v2");
    public static bool LegacyDemo(string[] args) => args.Contains("--demo") &&
        !args.Any(a => a is "--devices" or "--devices-preview" or "--devices-startup-smoke");

    public static DeviceStartupOptions Parse(string[] args, string localAppData)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal); string? directory = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--data-dir")
            {
                if (directory is not null || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("保存先の指定が不足または重複しています");
                directory = Path.GetFullPath(args[i]);
            }
            else if (arg is "--devices" or "--service" or "--tray" or "--devices-startup-smoke" or "--demo")
            { if (!flags.Add(arg)) throw new ArgumentException("起動指定が重複しています"); }
            else throw new ArgumentException("未対応の汎用GUI起動指定です: " + arg);
        }
        var smoke = flags.Contains("--devices-startup-smoke");
        if (smoke != flags.Contains("--demo") || smoke && (directory is null || flags.Contains("--service") || flags.Contains("--devices")))
            throw new ArgumentException("初回起動の試験には --devices-startup-smoke --demo --data-dir が必要です");
        return new(directory ?? DefaultDirectory(localAppData), flags.Contains("--tray"), smoke);
    }
}
