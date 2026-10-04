using System.Text.Json;
using System.Text.Json.Nodes;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbCapturedPathTests
{
    private const string First = @"\\?\hid#vid_26ce&pid_01a2#one";
    private const string Second = @"\\?\hid#vid_26ce&pid_01a2#two";
    [Fact] public void BindingIsAnImmutableCanonicalSnapshotAndChangesTheProtectedDigest()
    {
        var template = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6742);
        var paths = new[] { Second.ToUpperInvariant(), First };
        var bound = template.WithCapturedAsrockPaths(paths); paths[0] = "changed";
        Assert.Equal(new[] { First, Second }, bound.CapturedHidPaths); Assert.Empty(template.CapturedHidPaths);
        Assert.NotEqual(template.SettingsDigest, bound.SettingsDigest);
        using var json = JsonDocument.Parse(bound.Settings);
        var limits = json.RootElement.GetProperty("ASRock Polychrome USB").GetProperty("FlowDeck");
        Assert.Equal(1, limits.GetProperty("version").GetInt32());
        Assert.Equal(bound.CapturedHidPaths, limits.GetProperty("hid_paths").EnumerateArray().Select(v => v.GetString()));
    }
    public static IEnumerable<object[]> InvalidPaths()
    {
        yield return [new[] { First, First.ToUpperInvariant() }];
        yield return [new[] { "" }]; yield return [new[] { "path\0suffix" }];
        yield return [new[] { "path\nsuffix" }]; yield return [new[] { "path suffix" }];
        yield return [new[] { "path\u007fsuffix" }]; yield return [new[] { "機器" }];
        yield return [new[] { new string('a', 2049) }]; yield return [new string[] { null! }];
        yield return [Enumerable.Range(0, 33).Select(i => "path" + i).ToArray()];
    }
    [Theory] [MemberData(nameof(InvalidPaths))]
    public void InvalidOrAmbiguousCapturedPathsCannotBeSerialized(string[] paths) => Assert.Throws<IOException>(() =>
        new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6742).WithCapturedAsrockPaths(paths));
    [Fact] public void NoDetectionCannotBeConvertedIntoDeviceDetectionBySupplyingPaths() => Assert.Throws<IOException>(() =>
        new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, 6742).WithCapturedAsrockPaths([First]));
    [Theory] [InlineData("missing")] [InlineData("version")] [InlineData("added")] [InlineData("replaced")]
    [InlineData("duplicate")] [InlineData("type")] [InlineData("unknown-key")]
    public void ChangedLiveAllowlistIsRejected(string change)
    {
        var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6742).WithCapturedAsrockPaths([First]);
        var settings = JsonNode.Parse(profile.Settings)!.AsObject();
        OpenRgbSdkEnvironment Environment()
        {
            JsonElement Part(string name) => JsonSerializer.SerializeToElement(settings[name]);
            return new(new(Guid.NewGuid(), Guid.NewGuid(), 1, "receipt"), 25, Part("Detectors"),
                JsonSerializer.SerializeToElement(new { hid_safe_mode = new { type = "bool" } }), Part("Drivers"), Part("Server"), null,
                true, new(Part("Client"), Part("ProfileManager"), Part("QMKOpenRGBDevices"), Part("QMKVialRGBDevices"), Part("ASRock Polychrome USB")));
        }
        profile.CheckLiveEnvironment(Environment());
        var limits = settings["ASRock Polychrome USB"]!["FlowDeck"]!.AsObject();
        switch (change)
        {
            case "missing": settings["ASRock Polychrome USB"]!.AsObject().Remove("FlowDeck"); break;
            case "version": limits["version"] = 2; break;
            case "added": limits["hid_paths"] = new JsonArray(First, Second); break;
            case "replaced": limits["hid_paths"] = new JsonArray(Second); break;
            case "duplicate": limits["hid_paths"] = new JsonArray(First, First); break;
            case "type": limits["hid_paths"] = First; break;
            case "unknown-key": limits["all_devices"] = true; break;
        }
        Assert.Throws<IOException>(() => profile.CheckLiveEnvironment(Environment()));
    }
    [Fact] public async Task ProductDetectionRemainsClosedUntilSdkAndDetectorIntegrationIsAccepted()
    {
        using var directory = new ServiceTestDirectory();
        var error = await Assert.ThrowsAsync<IOException>(() => OpenRgbOwnedProcess.StartAsync(
            Path.Combine(directory.Path, "absent-runtime"), Path.Combine(directory.Path, "absent-config"),
            new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6742).WithCapturedAsrockPaths([First]), () => Task.CompletedTask));
        Assert.Contains("専用版", error.Message); Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }
}
