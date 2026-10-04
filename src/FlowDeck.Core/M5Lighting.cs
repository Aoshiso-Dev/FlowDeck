namespace FlowDeck.Core;

public record M5LightingItem(string Id, string Name);
public record M5LightingView(string Stamp, string ScopeId, string ScopeName, bool Ready, bool Busy,
    string? Error, int Brightness, bool Off, bool CanRestore, bool Synchronized,
    M5LightingItem[] Groups, M5LightingItem[] Scenes);
public record M5LightingCommand(string Id, string Command, string ScopeId, string ExpectedStamp,
    string? SceneId = null, int? Brightness = null)
{
    public static bool IsCommand(string? name) => name is "ApplyLightingScene" or "SetLightingBrightness" or
        "LightingOff" or "RestoreLighting" or "StopLightingSync";
    public static bool ValidId(string? id) => id is not null && Guid.TryParseExact(id, "N", out var parsed) && parsed.ToString("N") == id;
    public static bool ValidScope(string? id) => id == "all" || ValidId(id);
    public bool Valid => IsCommand(Command) && ValidScope(ScopeId) && ExpectedStamp.Length is > 0 and <= 64 &&
        (Command == "ApplyLightingScene" ? ValidId(SceneId) : SceneId is null) &&
        (Command == "SetLightingBrightness" ? Brightness is >= 1 and <= 100 : Brightness is null);
}

/// <summary>Host lighting ownership stays outside the transport and fan coordinator.</summary>
public interface IM5LightingBackend
{
    M5LightingView Read(string scopeId);
    Task<OperationResult> ExecuteAsync(M5LightingCommand request, CancellationToken cancellationToken);
}
