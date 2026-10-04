using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

internal static class ServiceSetupPlanning
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || args[0] != "--setup-plan") throw new ArgumentException("--setup-plan install|update|remove|rollback [--package 絶対パス] [--user SID]");
        string? packagePath = null, seedPath = null, runtime = null; var users = new List<string>(); var emptySettings = false;
        for (var i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("導入確認の引数が不足しています");
            if (args[i] == "--package" && packagePath is null && Path.IsPathFullyQualified(args[i + 1])) packagePath = args[i + 1];
            else if (args[i] == "--seed" && seedPath is null) seedPath = args[i + 1];
            else if (args[i] == "--empty-settings" && !emptySettings && args[i + 1] == "true") emptySettings = true;
            else if (args[i] == "--runtime" && runtime is null) runtime = args[i + 1];
            else if (args[i] == "--user") users.Add(args[i + 1]);
            else throw new ArgumentException("未対応または重複した導入確認の引数です");
        }
        var plan = ServiceSetupInspection.Create(args[1], packagePath, users, seedPath, emptySettings, runtime);
        Console.WriteLine(JsonSerializer.Serialize(plan, SettingsTransfer.Indented));
        return plan.ReadyForAdministratorReview ? 0 : 2;
    }
}
