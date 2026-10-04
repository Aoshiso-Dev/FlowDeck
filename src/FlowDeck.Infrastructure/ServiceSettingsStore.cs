using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public record ServiceSavedSettings(int Version, ServiceFanConfiguration Fans, FanPolicy? LastConfirmed,
    LightingState Lighting, Dictionary<string, HubPortLighting> LightingPorts);

/// <summary>Machine-owned cooling settings only; no user secrets or executable paths.</summary>
public sealed class ServiceSettingsStore(string directory)
{
    private readonly object sync = new();
    private readonly string path = Path.Combine(directory, "cooling-settings.json");
    public AppSettings Load()
    {
        lock (sync)
        {
            if (!File.Exists(path)) return new();
            var saved = ServiceEnvironment.Read<ServiceSavedSettings>(path);
            if (saved.Version != 1 || saved.Fans is null || saved.Lighting is null || saved.LightingPorts is null || saved.LightingPorts.Count > 5 ||
                saved.LightingPorts.Any(p => p.Key is not ("led1" or "led2" or "led3" or "led4" or "led5") || p.Value is null ||
                    string.IsNullOrWhiteSpace(p.Value.HubId) || p.Value.Effect is { } effect && !HubLighting.Valid(effect)))
                throw new IOException("サービスの冷却設定が不正です");
            var settings = saved.Fans.Merge(new()) with { LastConfirmedPolicy = saved.LastConfirmed, Lighting = saved.Lighting, LightingPorts = saved.LightingPorts };
            var portable = SettingsTransfer.FromFiles(new Dictionary<string, byte[]>());
            SettingsTransfer.Validate(portable with { Settings = SettingsTransfer.Portable(settings) });
            return settings;
        }
    }
    public void Save(AppSettings settings)
    {
        lock (sync) ServiceEnvironment.Write(path, new ServiceSavedSettings(1, ServiceFanConfiguration.From(settings),
            settings.LastConfirmedPolicy, settings.Lighting, settings.LightingPorts));
    }
}
