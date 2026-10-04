using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

internal static class ServiceSetupApply
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Do this before any file, service, or process mutation. There is intentionally no RunAs path.
        using var user = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("適用・復旧は管理者の端末が必要です。自動昇格は行いません");
        if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(ServiceEnvironment.InstallationDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("導入先の実行ファイルでは入れ替えできません。別フォルダーの検証済み配布物から実行してください");
        var engine = new ServiceDeployment(new WindowsServiceDeploymentControl());
        if (args is ["--setup-recover"])
        { Console.WriteLine(JsonSerializer.Serialize(await engine.RecoverAsync(), SettingsTransfer.Indented)); return 0; }
        if (args.Length < 2 || args[0] != "--setup-apply") throw new ArgumentException("--setup-apply install|update|remove|rollback --package 絶対パス --manifest-sha256 HASH --user SID");
        string? package = null, manifest = null, seedPath = null, seedHash = null, runtime = null; var users = new List<string>(); var emptySettings = false;
        for (var i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("適用の引数が不足しています");
            var value = args[i + 1];
            switch (args[i])
            {
                case "--package" when package is null && Path.IsPathFullyQualified(value): package = value; break;
                case "--manifest-sha256" when manifest is null: manifest = value; break;
                case "--seed" when seedPath is null && Path.IsPathFullyQualified(value): seedPath = value; break;
                case "--seed-sha256" when seedHash is null: seedHash = value; break;
                case "--empty-settings" when !emptySettings && value == "true": emptySettings = true; break;
                case "--runtime" when runtime is null: runtime = value; break;
                case "--user": users.Add(value); break;
                default: throw new ArgumentException("未対応または重複した適用の引数です。パスは絶対パスで指定してください");
            }
        }
        if (users.Count == 0) throw new ArgumentException("事前確認した操作ユーザーSIDを明示してください");
        AppSettings? seed = null;
        if (emptySettings && (seedPath is not null || seedHash is not null)) throw new ArgumentException("設定移行と空の設定を同時に指定できません");
        if (seedPath is not null || seedHash is not null)
        {
            if (seedPath is null || seedHash is null) throw new ArgumentException("初期設定のファイルとSHA-256を両方指定してください");
            seed = ServiceSeed.Read(seedPath, seedHash).Settings;
        }
        var result = await engine.ApplyAsync(args[1], package, manifest, users.ToArray(), seed, emptySettings, runtime);
        Console.WriteLine(JsonSerializer.Serialize(result, SettingsTransfer.Indented)); return 0;
    }
}
