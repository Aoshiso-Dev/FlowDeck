using System.Security.Principal;

namespace FlowDeck.Infrastructure;

public static class M5Firewall
{
    public static void MigrateLegacy(string executable)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        var current = Path.GetFullPath(executable);
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(current));
        if (parent is null) return;
        var previous = Path.Combine(parent, ApplicationDataMigration.LegacyDirectory, "NzxtControl.exe");
        foreach (var (suffix, protocol, port) in new[] { ("TLS", 6, "47631"), ("Discovery", 17, "47632") })
        {
            var legacyName = ApplicationDataMigration.LegacyDirectory + " M5GO " + suffix;
            dynamic? legacy = Find(policy, legacyName);
            if (legacy is null || !(bool)legacy.Enabled || (int)legacy.Profiles != 2 || (int)legacy.Direction != 1 ||
                (int)legacy.Action != 1 || (int)legacy.Protocol != protocol || (string)legacy.LocalPorts != port ||
                !string.Equals((string)legacy.RemoteAddresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals((string)legacy.ApplicationName, previous, StringComparison.OrdinalIgnoreCase)) continue;
            RequireAdministrator();
            var name = "FlowDeck M5GO " + suffix;
            if (Find(policy, name) is null) Add(policy, name, current, protocol, port);
            policy.Rules.Remove(legacyName);
        }
    }
    private static object? Find(dynamic policy, string name)
    {
        foreach (dynamic rule in policy.Rules) if ((string)rule.Name == name) return rule;
        return null;
    }
    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("管理者用の起動コマンドからアプリを起動して、通信を許可してください");
    }
    private static void Add(dynamic policy, string name, string executable, int protocol, string port)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        rule.Name = name; rule.Description = "M5GO local network connection";
        rule.ApplicationName = Path.GetFullPath(executable); rule.Protocol = protocol; rule.LocalPorts = port;
        rule.RemoteAddresses = "LocalSubnet"; rule.Profiles = 2; rule.Direction = 1; rule.Action = 1; rule.Enabled = true;
        policy.Rules.Add(rule);
    }
    public static void Allow(string executable)
    {
        RequireAdministrator();
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        foreach (var (suffix, protocol, port) in new[] { ("TLS", 6, "47631"), ("Discovery", 17, "47632") })
        {
            var name = "FlowDeck M5GO " + suffix;
            if (Find(policy, name) is not null) policy.Rules.Remove(name);
            Add(policy, name, executable, protocol, port);
        }
    }
}
