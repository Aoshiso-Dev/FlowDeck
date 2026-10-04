using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

// Opt-in process boundary verification. Neither mode constructs a real hardware provider.
internal static class DeviceDemoProcess
{
    public static async Task<int> RunHostAsync(string[] args)
    {
        if (args.Length != 5 || args[1] != "--pipe" || !DeviceWorkspaceOptions.DemoPipe(args[2]) || args[3] != "--data-dir" || !Path.IsPathFullyQualified(args[4]))
            throw new ArgumentException("--devices-demo-host --pipe 専用デモ名 --data-dir 専用絶対パス");
        await using var host = await DemoDeviceWorkspace.CreateAsync(args[4], args[2], persistent: true);
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, demo = true, processId = Environment.ProcessId, epoch = host.Runtime.Epoch, pipe = host.PipeName }, Contract.Json));
        while (true)
        {
            var input = Console.In.ReadLineAsync();
            if (await Task.WhenAny(input, host.Completion) == host.Completion)
            { await host.Completion; throw new IOException("汎用デモサービスの通信が終了しました"); }
            var command = await input;
            if (command is null or "stop") break;
            if (command == "state") Console.WriteLine(JsonSerializer.Serialize(new { state = host.Runtime.State() }, Contract.Json));
            else throw new ArgumentException("未対応のデモ操作です");
        }
        await host.DisposeAsync(); Console.WriteLine("{\"stopped\":true}"); return 0;
    }
    public static async Task<int> RunClientAsync(string[] args)
    {
        if (args.Length != 5 || args[1] != "--pipe" || !DeviceWorkspaceOptions.DemoPipe(args[2]) || args[3] != "--server-pid" ||
            !int.TryParse(args[4], out var pid) || pid < 1) throw new ArgumentException("--devices-demo-client --pipe 専用デモ名 --server-pid 確認済みPID");
        await using var client = new DeviceWorkspaceClient(DeviceServicePipeClient.ForDemo(args[2], pid));
        await client.ConnectAsync(); var snapshot = client.Snapshot!;
        var configured = snapshot.Configuration.Configuration.Cooling.Endpoints.First();
        var draft = new DeviceCoolingDraft(configured.Target);
        draft.Update(snapshot.State, snapshot.Configuration.Generation, configured, snapshot.ElapsedSeconds, snapshot.Connected);
        draft.SetDuty(64); var result = await client.ApplyCoolingAsync(draft.Capture());
        if (result.Outcome != Outcome.Confirmed) throw new IOException(result.Message);
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, demo = true, processId = Environment.ProcessId, duty = 64, target = configured.Target }, Contract.Json));
        await Console.In.ReadLineAsync();
        if (!(await client.ReleaseAsync()).Success) throw new IOException("模擬冷却の終了を確認できません");
        return 0;
    }
}
