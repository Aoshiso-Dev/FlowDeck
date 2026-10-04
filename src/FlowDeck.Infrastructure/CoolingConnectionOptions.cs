using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed record CoolingConnectionOptions(bool UseService = false, string? DemoPipe = null, int DemoServerPid = 0)
{
    public CoolingServicePipeClient CreateClient() => DemoPipe is null ? new() : CoolingServicePipeClient.ForDemo(DemoPipe, DemoServerPid);
    public static CoolingConnectionOptions Resolve(string[] args, string preference, bool installed)
    {
        if (preference is not ("auto" or "direct" or "service")) throw new IOException("冷却の接続方式が不正です");
        if (args.Contains("--direct") && args.Contains("--service")) throw new ArgumentException("接続方式は一つだけ指定してください");
        var pipeIndex = Array.IndexOf(args, "--demo-service-pipe"); var pidIndex = Array.IndexOf(args, "--demo-service-pid");
        if (pipeIndex >= 0 || pidIndex >= 0)
        {
            if (!args.Contains("--demo") || !args.Contains("--smoke") || !args.Contains("--data-dir") || args.Contains("--direct") ||
                pipeIndex < 0 || pipeIndex + 1 >= args.Length || pidIndex < 0 || pidIndex + 1 >= args.Length || !int.TryParse(args[pidIndex + 1], out var pid))
                throw new ArgumentException("サービスの画面試験には隔離したデモ用引数が必要です");
            _ = CoolingServicePipeClient.ForDemo(args[pipeIndex + 1], pid);
            return new(true, args[pipeIndex + 1], pid);
        }
        if (args.Contains("--smoke"))
        {
            if (args.Contains("--service")) throw new ArgumentException("デモ試験から実機サービスへ接続できません");
            return new();
        }
        return new(args.Contains("--service") || !args.Contains("--direct") && (preference == "service" || preference == "auto" && installed));
    }
}
public sealed class CoolingConnectionPreferenceStore(string directory)
{
    private sealed record Preference(int Version, string Mode);
    private string PathName => Path.Combine(directory, "cooling-connection.json");
    public string Load()
    {
        if (!File.Exists(PathName)) return "auto";
        var value = ServiceEnvironment.Read<Preference>(PathName);
        if (value.Version != 1 || value.Mode is not ("auto" or "direct" or "service")) throw new IOException("冷却の接続方式が不正です");
        return value.Mode;
    }
    public void Save(string mode)
    { if (mode is not ("auto" or "direct" or "service")) throw new ArgumentException("接続方式が不正です"); ServiceEnvironment.Write(PathName, new Preference(1, mode)); }
}
