using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public static class DeviceSettingsDifferences
{
    public static string[] Describe(DeviceConfigurationPackage before,DeviceConfigurationPackage after)
    {
        var lines=new List<string>();
        static bool Same<T>(T a,T b)=>SettingsTransfer.Hash(a)==SettingsTransfer.Hash(b);
        static string Name(DeviceConfigurationPackage config,EndpointReference target)
        { var d=config.Devices.Devices.SingleOrDefault(d=>d.Id==target.DeviceId); return (d?.Name??"未関連")+" / "+(d?.Endpoints.SingleOrDefault(e=>e.Id==target.EndpointId)?.Name??target.EndpointId); }
        static string Duty(int? value)=>value is { } v?v+"%":"未設定";
        static string Yes(bool value)=>value?"有効":"無効";
        static string Safety(CoolingSafety? s)=>s is null?"未登録":$"最低 {Duty(s.Minimum)}・異常時 {Duty(s.Fallback)}・終了時 "+
            (s.ExitAction==CoolingExitAction.ReleaseToDevice?"機器管理":Duty(s.Exit));
        static string Program(LightingProgram p)=>(p.EffectId switch { "fixed"=>"固定","off"=>"消灯","breathing"=>"呼吸","color-cycle"=>"カラーサイクル",_=>"内蔵効果 "+p.EffectId })+
            " "+string.Join(" / ",p.Colors.Select(c=>$"#{c.R:X2}{c.G:X2}{c.B:X2}"))+$" 明るさ {p.Brightness}%・速さ {p.Speed}"+(p.Reverse?"・逆向き":"")+(p.LedColors is null?"":$"・LED別配色 {p.LedColors.Length}個");
        static string Exit(LightingExitPolicy? policy)=>policy?.Action switch {
            LightingExitAction.Off=>"消灯",LightingExitAction.RestoreOriginal=>"元の設定",LightingExitAction.FixedColor=>$"固定 #{policy.Color!.R:X2}{policy.Color.G:X2}{policy.Color.B:X2}",_=>"未設定" };
        lines.Add($"登録機器 {before.Devices.Devices.Length} → {after.Devices.Devices.Length} / 監視項目 {before.Dashboard.Items.Length} → {after.Dashboard.Items.Length}");
        foreach(var removed in before.Devices.Devices.Where(d=>after.Devices.Devices.All(n=>n.Id!=d.Id))) lines.Add("機器登録を置換・削除: "+removed.Name);
        foreach(var device in after.Devices.Devices)
        {
            var old=before.Devices.Devices.SingleOrDefault(d=>d.Id==device.Id);
            if(old?.Name!=device.Name) lines.Add("機器名: "+(old?.Name??"未登録")+" → "+device.Name);
            foreach(var endpoint in device.Endpoints)
            {
                var previous=old?.Endpoints.SingleOrDefault(e=>e.Id==endpoint.Id);
                if(Same(previous,endpoint)) continue;
                lines.Add("端子 / "+device.Name+": "+(previous?.Name??"未登録")+" → "+endpoint.Name);
                if(endpoint.Setup is { } setup) lines.Add($"接続機器 {setup.Accessory} / LED数 {setup.LedCount?.ToString()??"未設定"}・"+
                    (setup.LedCountConfirmed?"確認済み":"再確認が必要")+" / M5GO表示 "+Yes(setup.ShowOnM5));
            }
        }
        foreach(var endpoint in after.Cooling.Endpoints)
        {
            var previous=before.Cooling.Endpoints.SingleOrDefault(e=>e.Target==endpoint.Target);
            if(Same(previous,endpoint)) continue;
            lines.Add("冷却 / "+Name(after,endpoint.Target)+": "+Safety(previous?.Safety)+" → "+Safety(endpoint.Safety));
            lines.Add(endpoint.WiringConfirmed?"配線確認を保持":"配線と出力値の再確認が必要");
            if(endpoint.Safety.Stop is { } stop) lines.Add($"停止条件あり / 再始動 {stop.RestartDuty}%・{stop.RestartSeconds:0.#}秒 / "+string.Join(" / ",stop.Temperatures.Select(r=>Name(after,r.Sensor)+$" {r.Below:0.#}°C未満")));
            else if(previous?.Safety.Stop is not null) lines.Add("停止条件を解除");
            lines.Add("固定出力の温度保護: "+(endpoint.Safety.ManualProtection.Length==0?"なし":string.Join(" / ",endpoint.Safety.ManualProtection.Select(r=>Name(after,r.Sensor)+$" 過熱 {r.Overheat:0.#}°C・復帰 {r.ResumeBelow:0.#}°C未満"))));
            foreach(var removed in (previous?.Profiles??[]).Where(p=>endpoint.Profiles.All(n=>n.Id!=p.Id))) lines.Add("カーブ削除: "+removed.Name);
            foreach(var p in endpoint.Profiles)
            {
                if(previous?.Profiles.SingleOrDefault(o=>o.Id==p.Id) is { } old&&Same(old,p)) continue;
                lines.Add($"カーブ {p.Name} / 下降待ち {p.DescentDelaySeconds:0.#}秒・毎秒 {p.DescentPerSecond:0.#}%");
                foreach(var c in p.Curves) lines.Add(Name(after,c.Sensor)+": "+string.Join(" → ",c.Points.Select(v=>$"{v.Temperature:0.#}°C/{v.Duty}%"))+$" / 過熱 {c.Overheat:0.#}°C・復帰 {c.ResumeBelow:0.#}°C未満");
            }
        }
        foreach(var removed in before.Cooling.Endpoints.Where(e=>after.Cooling.Endpoints.All(n=>n.Target!=e.Target))) lines.Add("冷却設定を削除: "+Name(before,removed.Target));
        if(!Same(before.Cooling.ProfileTemplates,after.Cooling.ProfileTemplates)) lines.Add("カーブのテンプレート: "+
            string.Join(" / ",(before.Cooling.ProfileTemplates??[]).Select(p=>p.Name))+" → "+string.Join(" / ",(after.Cooling.ProfileTemplates??[]).Select(p=>p.Name)));
        foreach(var endpoint in after.Lighting.Endpoints??[])
        {
            var previous=before.Lighting.Endpoints?.SingleOrDefault(e=>e.Target==endpoint.Target);
            if(!Same(previous,endpoint)) lines.Add("照明設定 / "+Name(after,endpoint.Target)+": 終了時 "+Exit(previous?.Exit)+" → "+Exit(endpoint.Exit)+
                " / "+(endpoint.WiringConfirmed?"配線確認を保持":"配線の再確認が必要"));
        }
        foreach(var removed in (before.Lighting.Endpoints??[]).Where(e=>!(after.Lighting.Endpoints??[]).Any(n=>n.Target==e.Target))) lines.Add("照明設定を削除: "+Name(before,removed.Target));
        foreach(var alias in after.Lighting.Names)
            if(before.Lighting.Names.SingleOrDefault(a=>a.Target==alias.Target) is not { } old||old.Name!=alias.Name) lines.Add("照明名 / "+Name(after,alias.Target)+": "+alias.Name);
        foreach(var removed in before.Lighting.Names.Where(a=>!after.Lighting.Names.Any(n=>n.Target==a.Target))) lines.Add("照明名を解除: "+removed.Name);
        if(!Same(before.Lighting.DefaultProgram,after.Lighting.DefaultProgram)) lines.Add("照明の既定値: "+(after.Lighting.DefaultProgram is { } preset?Program(preset):"未設定"));
        foreach(var pref in after.Lighting.Preferences??[])
            if(before.Lighting.Preferences?.SingleOrDefault(p=>p.Target==pref.Target) is not { } old||!Same(old,pref)) lines.Add("照明 / "+Name(after,pref.Target)+": "+Program(pref.Program));
        foreach(var scene in after.Lighting.Scenes)
            if(before.Lighting.Scenes.SingleOrDefault(s=>s.Id==scene.Id) is not { } old||!Same(old,scene))
            {
                lines.Add("シーン: "+scene.Name);
                foreach(var light in scene.Individual) lines.Add(Name(after,light.Target)+": "+Program(light.Program));
                foreach(var group in scene.Synchronized) lines.Add("同期: "+string.Join(" / ",group.Targets.Select(t=>Name(after,t)))+": "+Program(group.Program));
            }
        foreach(var scene in before.Lighting.Scenes.Where(s=>after.Lighting.Scenes.All(n=>n.Id!=s.Id))) lines.Add("シーン削除: "+scene.Name);
        foreach(var removed in (before.Lighting.Preferences??[]).Where(p=>!(after.Lighting.Preferences??[]).Any(n=>n.Target==p.Target))) lines.Add("照明の選択を解除: "+Name(before,removed.Target));
        if(!Same(before.Lighting.SyncTargets,after.Lighting.SyncTargets)||!Same(before.Lighting.SavedSynchronization,after.Lighting.SavedSynchronization)||!Same(before.Lighting.SavedFrameGroups,after.Lighting.SavedFrameGroups))
            lines.Add("照明の同期構成を変更 / 対象: "+(after.Lighting.SyncTargets.Length==0?"なし":string.Join(" / ",after.Lighting.SyncTargets.Select(t=>Name(after,t)))));
        if(!Same(before.Lighting.TargetAdjustments,after.Lighting.TargetAdjustments)||!Same(before.Lighting.Adjustments,after.Lighting.Adjustments))
            lines.Add("照明の全体・グループ・個別の明るさと消灯・復帰用設定を変更");
        foreach(var group in after.Cooling.Groups.Concat(after.Lighting.Groups))
            if(before.Cooling.Groups.Concat(before.Lighting.Groups).SingleOrDefault(g=>g.Kind==group.Kind&&g.Id==group.Id) is not { } old||!Same(old,group))
                lines.Add("グループ "+group.Name+": "+string.Join(" / ",group.Targets.Select(t=>Name(after,t))));
        foreach(var removed in before.Cooling.Groups.Concat(before.Lighting.Groups).Where(g=>!after.Cooling.Groups.Concat(after.Lighting.Groups).Any(n=>n.Kind==g.Kind&&n.Id==g.Id)))
            lines.Add("グループ削除: "+removed.Name);
        if(!Same(before.Dashboard,after.Dashboard)) foreach(var item in after.Dashboard.Items) lines.Add("表示 / "+Name(after,item.Target)+": "+item.Name+(item.Visible?"・表示":"・非表示"));
        static string Theme(string value)=>value switch { "dark"=>"ダーク","light"=>"ライト",_=>"システム" };
        if(!Same(before.Preferences.Ui,after.Preferences.Ui)) lines.Add("外観: "+Theme(before.Preferences.Ui.Theme)+" → "+Theme(after.Preferences.Ui.Theme)+"（ウィンドウ配置は次回起動時）");
        if(!Same(before.Preferences.Notifications,after.Preferences.Notifications))
        {
            var notifications=after.Preferences.Notifications;
            lines.Add($"通知 {Yes(notifications.Enabled)}・デスクトップ {Yes(notifications.Desktop)} / 遅延 {notifications.DelaySeconds}秒・復帰待ち {notifications.RecoverySeconds}秒・繰り返し {notifications.RepeatSeconds}秒");
            foreach(var rule in notifications.Rules??[]) lines.Add("通知条件 / "+rule.Name+": "+Name(after,rule.Target)+$" {rule.Threshold:0.#}"+
                (rule.Measurement==DeviceFeature.Temperature?"°C以上":" RPM未満")+"・"+Yes(rule.Enabled));
            foreach(var removed in (before.Preferences.Notifications.Rules??[]).Where(r=>!(notifications.Rules??[]).Any(n=>n.Id==r.Id))) lines.Add("通知条件を削除: "+removed.Name);
        }
        if(!Same(before.Preferences.Palette,after.Preferences.Palette)) lines.Add($"お気に入り色 {before.Preferences.Palette.Favorites.Length} → {after.Preferences.Palette.Favorites.Length}色 / 最近使った色を更新");
        if(before.Preferences.Startup.ResumeControl != after.Preferences.Startup.ResumeControl) lines.Add("起動時の制御再開 "+Yes(after.Preferences.Startup.ResumeControl));
        if(!Same(before.Preferences.M5,after.Preferences.M5)) lines.Add("M5GOの接続・表示設定を変更");
        if(before.Preferences.M5ConnectOnStart!=after.Preferences.M5ConnectOnStart) lines.Add("M5GOの自動接続 "+Yes(after.Preferences.M5ConnectOnStart));
        if(!Same(before.Preferences.M5Selection,after.Preferences.M5Selection))
        {
            var panel=after.Preferences.M5Selection;
            lines.Add("M5GO CPU: "+(panel?.Cpu is { } cpu?Name(after,cpu):"未選択")+" / GPU: "+(panel?.Gpu is { } gpu?Name(after,gpu):"未選択"));
            lines.Add("M5GO Hub: "+(panel?.Hub is { } hub?after.Devices.Devices.Single(d=>d.Id==hub.DeviceId).Name:"未選択")+
                " / ファン: "+string.Join(", ",panel?.Fans??[])+" / 照明: "+string.Join(", ",panel?.Leds??[]));
            lines.Add("M5GO マザー表示: "+string.Join(", ",(panel?.Motherboard??[]).Select(i=>i.Name)));
            lines.Add("M5GO プリセット: "+string.Join(", ",(panel?.Profiles??[]).Select(p=>p.Slot+" = "+p.ProfileId)));
        }
        if(!Same(before.Preferences.Providers,after.Preferences.Providers)) lines.Add("外部照明の接続設定を変更");
        return lines.ToArray();
    }
}
