using System.Diagnostics;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

internal static class MemorySyncCheck
{
    public static async Task RunAsync(string output)
    {
        var apps=Process.GetProcessesByName("FlowDeck");
        try { if (apps.Length > 0) throw new IOException("FlowDeckを通常終了してください"); }
        finally { foreach (var app in apps) app.Dispose(); }
        var path=Path.GetFullPath(output); if (File.Exists(path) || File.Exists(path+".before.json")) throw new IOException("Use a fresh diagnostic path");
        var control = new EneMemoryRgbControl(recoveryDirectory:path+".recovery"); var before=await control.ReadAsync();
        if (before.Devices.Length != 2 || before.Devices.Any(d => d.Zones[0].Configuration is null)) throw new IOException("メモリ2枚の設定を確認できません");
        var json=new JsonSerializerOptions(Contract.Json) { WriteIndented=true };
        await File.WriteAllTextAsync(path+".before.json",JsonSerializer.Serialize(before,json));
        string? lease=null,error=null; var times=new List<double>(); bool restored=false;
        try
        {
            lease=await control.BeginAsync(before.Devices.Select(d => new MemoryRgbTarget(d.ControlIdentity!,d.Zones[0].Configuration!)).ToArray());
            var origin=Stopwatch.GetTimestamp();
            for(var i=0;i<24;i++)
            {
                var start=Stopwatch.GetTimestamp(); var color=HubLighting.Frame("breathing",new(30,90,255),Stopwatch.GetElapsedTime(origin).TotalSeconds,4);
                await control.FrameAsync(lease,color); times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                var wait=100-Stopwatch.GetElapsedTime(start).TotalMilliseconds; if(wait>0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
            }
        }
        catch(Exception e) { error=e.ToString(); }
        finally
        {
            if(lease is not null) try { await control.EndAsync(lease,false); } catch(Exception e) { error=(error??"")+" / restore: "+e; }
            try
            {
                var after=await control.ReadAsync();
                restored=before.Devices.All(d => after.Devices.SingleOrDefault(a => a.ControlIdentity==d.ControlIdentity)?.Zones[0].Configuration==d.Zones[0].Configuration);
            } catch(Exception e) { error=(error??"")+" / readback: "+e; }
            var result=new {Frames=times.Count, AverageMilliseconds=times.Count>0?times.Average():0, MaximumMilliseconds=times.Count>0?times.Max():0, Restored=restored, Error=error, Completed=error is null && restored && times.Count==24};
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(result,json));
            Console.WriteLine(JsonSerializer.Serialize(result,json));
            if(!result.Completed) Environment.ExitCode=1;
        }
    }
}
