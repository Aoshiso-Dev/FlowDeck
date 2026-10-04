using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public sealed partial class DeviceM5Lighting
{
    public M5BodyLedView ReadBodyLed(string scopeId) => ReadBodyLed(client.Snapshot, scopeId);
    public static M5BodyLedView ReadBodyLed(DeviceWorkspaceSnapshot? snapshot, string scopeId)
    {
        try
        {
            if (snapshot is not { } s) return new(null, "サービスへ接続してください");
            var targets = Targets(s, Group(s, scopeId));
            if (Problem(s, targets) is { } error) return new(null, error);
            M5BodyLedFrame? chosen = null;
            foreach (var target in targets)
            {
                var state = s.State.Endpoints.Single(e => e.Target == target);
                if (!state.Owned || state.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified))
                    return new(null, "PCで照明を適用してください");
                M5BodyLedFrame frame;
                if (state.Lighting?.Running == true)
                {
                    if (state.Lighting.Mirror is not { Valid: true } mirror) return new(null, "同期の更新待ちです");
                    var remaining = (int)Math.Floor(mirror.ValidForMs - s.ElapsedSeconds * 1000);
                    frame = mirror with { PhaseMs = mirror.PeriodMs == 0 ? 0 :
                        (mirror.PhaseMs + s.ElapsedSeconds * 1000) % mirror.PeriodMs, ValidForMs = remaining };
                }
                else if (state.ReportedLighting is { } p && DeviceLighting.FrameProgram(p) && !DeviceLighting.Animated(p))
                    frame = new(p.EffectId, DeviceLighting.StopColor(p), 100, 0, 0, (int)Math.Floor(3000 - s.ElapsedSeconds * 1000));
                else return new(null, "機器固有の効果は本体同期に未対応です");
                if (!frame.Valid) return new(null, "照明の更新待ちです");
                if (chosen is not null && (chosen.Effect != frame.Effect || chosen.Color != frame.Color ||
                    chosen.Brightness != frame.Brightness || chosen.PeriodMs != frame.PeriodMs ||
                    Math.Min(Math.Abs(chosen.PhaseMs - frame.PhaseMs), frame.PeriodMs == 0 ? double.MaxValue :
                        frame.PeriodMs - Math.Abs(chosen.PhaseMs - frame.PhaseMs)) > 1))
                    return new(null, "色が異なります。対象グループを選んでください");
                chosen = frame;
            }
            return new(chosen);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return new(null, "照明の対象を選び直してください"); }
    }
}
