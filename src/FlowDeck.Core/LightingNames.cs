namespace FlowDeck.Core;

public record LightAddress(HubLightAddress? Hub = null, BoardLightAddress? Board = null)
{
    public bool Valid => (Hub is null) != (Board is null) &&
        (Hub is null || Identity(Hub.HubId) && Hub.Channel is "led1" or "led2" or "led3" or "led4" or "led5") &&
        (Board is null || Identity(Board.Identity) && Board.Zone <= 7);
    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && !value.Any(char.IsControl);
}
public record LightingName(LightAddress Address, string Name);
public record LightingNameCollection(int Version = 1, LightingName[]? Names = null);

public static class LightingNames
{
    public static string? Validate(LightingNameCollection? value) => value?.Version != 1 || value.Names is null || value.Names.Length > 256 ||
        value.Names.Any(n => n?.Address?.Valid != true || !ValidName(n.Name)) ||
        value.Names.Select(n => n.Address).Distinct().Count() != value.Names.Length ? "照明名の保存形式が不正または重複しています" : null;
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name.Length <= 40 && !name.Any(char.IsControl);
    public static string? Find(LightingNameCollection value, LightAddress address) => value.Names?.SingleOrDefault(n => n.Address == address)?.Name;
    public static LightingNameCollection Set(LightingNameCollection source, LightAddress address, string name)
    {
        if (Validate(source) is { } error) throw new ArgumentException(error);
        if (!address.Valid) throw new ArgumentException("照明の対象が不正です");
        var trimmed = name.Trim();
        if (trimmed.Length > 0 && !ValidName(trimmed)) throw new ArgumentException("名前は40文字以内で入力してください");
        var names = source.Names!.Where(n => n.Address != address).ToList();
        if (trimmed.Length > 0) names.Add(new(address, trimmed));
        var next = source with { Names = names.ToArray() };
        if (Validate(next) is { } invalid) throw new ArgumentException(invalid);
        return next;
    }
}
