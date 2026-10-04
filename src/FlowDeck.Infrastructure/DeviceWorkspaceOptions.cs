using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record DeviceWorkspaceOptions(string Directory, bool Smoke, string? PipeName, int? ServerProcessId, bool MonitoringSetup = false, bool EndpointSetup = false, bool CoolingSettings = false, bool SettingsTransfer = false, bool CoolingLifecycle = false, bool CoolingGroups = false, bool CoolingCalibration = false, bool Notifications = false, bool M5Settings = false, bool M5Connection = false, bool LightingLibrary = false, bool LightingLevels = false, bool NativeLighting = false, bool LightingPersonalization = false, bool LightingIdentification = false, bool OpenRgbInspection = false, bool Diagnostics = false, bool LoginStartup = false, bool ProviderRoutes = false, bool RegistrationRemoval = false, bool CacheRetirement = false)
{
    public bool External => PipeName is not null;
    public static bool DemoPipe(string? name) => name is not null && name.StartsWith("FlowDeck.Devices.Demo.", StringComparison.Ordinal) &&
        DeviceServiceProtocol.Id(name["FlowDeck.Devices.Demo.".Length..]);
    public static DeviceWorkspaceOptions Parse(string[] args)
    {
        var flags = new HashSet<string>(); var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--devices-preview" or "--demo" or "--smoke" or "--monitoring-setup" or "--endpoint-setup" or "--cooling-settings" or "--settings-transfer" or "--cooling-lifecycle" or "--cooling-groups" or "--cooling-calibration" or "--notifications" or "--m5-settings" or "--m5-connection" or "--lighting-library" or "--lighting-levels" or "--native-lighting" or "--lighting-personalization" or "--lighting-identification" or "--openrgb-inspection" or "--diagnostics" or "--login-startup" or "--provider-routes" or "--registration-removal" or "--cache-retirement")
            { if (!flags.Add(arg)) throw new ArgumentException("起動指定が重複しています"); }
            else if (arg is "--data-dir" or "--devices-pipe" or "--server-pid")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(arg, args[++i]))
                    throw new ArgumentException("接続指定が不足または重複しています");
            }
            else throw new ArgumentException("未対応の汎用GUI起動指定です: " + arg);
        }
        if (!flags.Contains("--devices-preview") || !flags.Contains("--demo") || !values.TryGetValue("--data-dir", out var directory) || string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("汎用GUIには --devices-preview --demo --data-dir の指定が必要です");
        var pipe = values.GetValueOrDefault("--devices-pipe"); var pidText = values.GetValueOrDefault("--server-pid"); int? pid = null;
        if (pipe is not null || pidText is not null)
        {
            if (!DemoPipe(pipe) || !int.TryParse(pidText, out var parsed) || parsed < 1) throw new ArgumentException("専用デモパイプと確認済みサービスのプロセスIDが必要です");
            pid = parsed;
        }
        var monitoring = flags.Contains("--monitoring-setup");
        if (monitoring && (!flags.Contains("--smoke") || pipe is not null)) throw new ArgumentException("初回監視の試験には組み込みデモの--smokeが必要です");
        var endpoint = flags.Contains("--endpoint-setup");
        if (endpoint && (!flags.Contains("--smoke") || pipe is not null || monitoring)) throw new ArgumentException("端子設定の試験には組み込みデモの--smokeが必要です");
        var cooling = flags.Contains("--cooling-settings");
        if (cooling && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint)) throw new ArgumentException("冷却編集の試験には組み込みデモの--smokeが必要です");
        var transfer = flags.Contains("--settings-transfer");
        if (transfer && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling)) throw new ArgumentException("設定復元の試験には組み込みデモの--smokeが必要です");
        var lifecycle=flags.Contains("--cooling-lifecycle");
        if(lifecycle&&(!flags.Contains("--smoke")||pipe is not null||monitoring||endpoint||cooling||transfer)) throw new ArgumentException("冷却の停止・起動試験には組み込みデモの--smokeが必要です");
        var groups=flags.Contains("--cooling-groups");
        if(groups&&(!flags.Contains("--smoke")||pipe is not null||monitoring||endpoint||cooling||transfer||lifecycle)) throw new ArgumentException("冷却グループ試験には組み込みデモの--smokeが必要です");
        var calibration=flags.Contains("--cooling-calibration");
        if(calibration&&(!flags.Contains("--smoke")||pipe is not null||monitoring||endpoint||cooling||transfer||lifecycle||groups)) throw new ArgumentException("端子校正試験には組み込みデモの--smokeが必要です");
        var notifications = flags.Contains("--notifications");
        if (notifications && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration))
            throw new ArgumentException("通知の画面試験には組み込みデモの--smokeが必要です");
        var m5 = flags.Contains("--m5-settings");
        if (m5 && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications))
            throw new ArgumentException("M5GO設定の画面試験には組み込みデモの--smokeが必要です");
        var m5Connection = flags.Contains("--m5-connection");
        if (m5Connection && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications || m5))
            throw new ArgumentException("M5GO接続の画面試験には組み込みデモの--smokeが必要です");
        var lightingLibrary = flags.Contains("--lighting-library");
        if (lightingLibrary && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications || m5 || m5Connection))
            throw new ArgumentException("照明編集の画面試験には組み込みデモの--smokeが必要です");
        var lightingLevels = flags.Contains("--lighting-levels");
        if (lightingLevels && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications || m5 || m5Connection || lightingLibrary))
            throw new ArgumentException("照明の明るさ試験には組み込みデモの--smokeが必要です");
        var nativeLighting = flags.Contains("--native-lighting");
        if (nativeLighting && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications || m5 || m5Connection || lightingLibrary || lightingLevels))
            throw new ArgumentException("機器内蔵効果の試験には組み込みデモの--smokeが必要です");
        var personalization = flags.Contains("--lighting-personalization");
        if (personalization && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications || m5 || m5Connection || lightingLibrary || lightingLevels || nativeLighting))
            throw new ArgumentException("照明名・色見本の試験には組み込みデモの--smokeが必要です");
        var identification = flags.Contains("--lighting-identification");
        if (identification && (!flags.Contains("--smoke") || pipe is not null || monitoring || endpoint || cooling || transfer || lifecycle || groups || calibration || notifications || m5 || m5Connection || lightingLibrary || lightingLevels || nativeLighting || personalization))
            throw new ArgumentException("照明識別の試験には組み込みデモの--smokeが必要です");
        var openRgb = flags.Contains("--openrgb-inspection");
        if (openRgb && (!flags.Contains("--smoke") || pipe is not null || flags.Any(f => f is not ("--devices-preview" or "--demo" or "--smoke" or "--openrgb-inspection"))))
            throw new ArgumentException("OpenRGB接続確認の画面試験には組み込みデモの--smokeが必要です");
        var diagnostics = flags.Contains("--diagnostics");
        if (diagnostics && (!flags.Contains("--smoke") || pipe is not null || flags.Any(f => f is not ("--devices-preview" or "--demo" or "--smoke" or "--diagnostics"))))
            throw new ArgumentException("診断の画面試験には組み込みデモの--smokeが必要です");
        var loginStartup = flags.Contains("--login-startup");
        if (loginStartup && (!flags.Contains("--smoke") || pipe is not null || flags.Any(f => f is not ("--devices-preview" or "--demo" or "--smoke" or "--login-startup"))))
            throw new ArgumentException("ログイン時起動の試験には組み込みデモの--smokeが必要です");
        var providerRoutes = flags.Contains("--provider-routes");
        if (providerRoutes && (!flags.Contains("--smoke") || pipe is not null || flags.Any(f => f is not ("--devices-preview" or "--demo" or "--smoke" or "--provider-routes"))))
            throw new ArgumentException("経路切替の画面試験には組み込みデモの--smokeが必要です");
        var registrationRemoval = flags.Contains("--registration-removal");
        if (registrationRemoval && (!flags.Contains("--smoke") || pipe is not null || flags.Any(f => f is not ("--devices-preview" or "--demo" or "--smoke" or "--registration-removal"))))
            throw new ArgumentException("登録解除の画面試験には組み込みデモの--smokeが必要です");
        var cacheRetirement = flags.Contains("--cache-retirement");
        if (cacheRetirement && (!flags.Contains("--smoke") || pipe is not null || flags.Any(f => f is not ("--devices-preview" or "--demo" or "--smoke" or "--cache-retirement"))))
            throw new ArgumentException("検出履歴整理の画面試験には組み込みデモの--smokeが必要です");
        return new(Path.GetFullPath(directory), flags.Contains("--smoke"), pipe, pid, monitoring, endpoint, cooling, transfer, lifecycle,groups,calibration,notifications,m5,m5Connection,lightingLibrary,lightingLevels,nativeLighting,personalization,identification,openRgb,diagnostics,loginStartup,providerRoutes,registrationRemoval,cacheRetirement);
    }
}
