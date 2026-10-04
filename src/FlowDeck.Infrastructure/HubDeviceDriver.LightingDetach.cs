using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class HubDeviceDriver
{
    private async Task<DeviceDriverReleaseResult[]> EndSelectedStreamAsync(HubStream stream,string[] endpoints,CancellationToken ct)
    {
        var selected=endpoints.ToArray(); await io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock(gate)
            {
                if(selected.Length==0 || selected.Distinct().Count()!=selected.Length || selected.Any(e=>!stream.Channels.Contains(e)))
                    throw new IOException("同期終了の端子が現在の対象と一致しません");
                if(stream.Ended || stream.Channels.Any(c=>lightingStreams.GetValueOrDefault(c)!=stream))
                    throw new IOException("照明ストリームの所有が変更されています");
            }
            if(selected.Length==stream.Channels.Length) return await EndStreamCoreAsync(stream,DeviceLightingStopReason.Normal,ct).ConfigureAwait(false);
            var results=new List<DeviceDriverReleaseResult>();
            foreach(var saved in stream.Obligations.Where(o=>selected.Contains(o.Channel)).ToArray())
            {
                DeviceDriverResult result;
                try
                {
                    if(!stream.HasWritten)
                    { lightingJournal.Complete(saved); result=new(Outcome.Confirmed,"照明出力を変更せず同期準備を終了しました"); }
                    else result=await ReleaseLightingOneAsync(saved.Channel,ct,saved,new("fixed",
                        [stream.NormalStopColors.GetValueOrDefault(saved.Channel)??DeviceLighting.StopColor(stream.Program)]),stream.Locator).ConfigureAwait(false);
                }
                catch(Exception e) { result=new(Outcome.Unknown,"照明の終了を確認できません: "+e.Message); }
                results.Add(new(saved.Channel,result));
                if(result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified)
                {
                    lock(gate)
                    {
                        if(lightingStreams.GetValueOrDefault(saved.Channel)==stream) lightingStreams.Remove(saved.Channel);
                        lightingFaulted.Remove(saved.Channel);
                        stream.Channels=stream.Channels.Where(c=>c!=saved.Channel).ToArray();
                        stream.Obligations=stream.Obligations.Where(o=>o.Channel!=saved.Channel).ToArray();
                        stream.NormalStopColors.Remove(saved.Channel);
                    }
                }
                else lock(gate) lightingFaulted.Add(saved.Channel);
            }
            return results.ToArray();
        }
        finally { io.Release(); }
    }
}
