using System.Text.Json;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbStartupProfileTests
{
    [Theory] [InlineData(0)] [InlineData(1)]
    public void EveryKnownDetectorHasAnExplicitChoiceAndDynamicAndAutomaticActionsAreEmpty(int kind)
    {
        var profile = new OpenRgbStartupProfile((OpenRgbStartupKind)kind, 6742);
        using var json = JsonDocument.Parse(profile.Settings); var root = json.RootElement;
        var detectors = root.GetProperty("Detectors").GetProperty("detectors");
        Assert.Equal(1911, detectors.EnumerateObject().Count());
        Assert.Equal(profile.EnabledDetectors, detectors.EnumerateObject().Where(p => p.Value.GetBoolean()).Select(p => p.Name));
        Assert.False(detectors.GetProperty("Gigabyte GeForce RTX 5090 GAMING OC").GetBoolean());
        Assert.False(detectors.GetProperty("Gigabyte GeForce RTX 5090 Gaming OC").GetBoolean());
        Assert.True(root.GetProperty("Detectors").GetProperty("hid_safe_mode").GetBoolean());
        Assert.Empty(root.GetProperty("Client").GetProperty("clients").EnumerateArray());
        foreach (var name in new[] { "QMKOpenRGBDevices", "QMKVialRGBDevices" }) Assert.Empty(root.GetProperty(name).GetProperty("devices").EnumerateArray());
        var profiles = root.GetProperty("ProfileManager").EnumerateObject().ToArray(); Assert.Equal(6, profiles.Length);
        Assert.All(profiles, p => { Assert.False(p.Value.GetProperty("enabled").GetBoolean()); Assert.Equal("", p.Value.GetProperty("name").GetString()); });
        Assert.Equal("127.0.0.1", root.GetProperty("Server").GetProperty("default_host").GetString());
        Assert.False(root.GetProperty("Server").GetProperty("all_controllers").GetBoolean());
    }
    [Fact] public void ReturnedSettingsCannotChangeTheCapturedStartup()
    {
        var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, 6742);
        var expected = profile.Settings; var copy = profile.Settings; Array.Fill(copy, (byte)0);
        Assert.Equal(expected, profile.Settings);
    }
    [Fact] public void HiddenHeadlessArgumentsDoNotInheritAUserPluginOrShellEnvironment()
    {
        using var d = new ServiceTestDirectory();
        var profile = new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, 6743);
        var start = profile.CreateStartInfo(Path.Combine(d.Path, "runtime with spaces"), Path.Combine(d.Path, "日本語 config"));
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow); Assert.Equal("", start.Verb);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Contains("--nodetect", start.ArgumentList); Assert.Contains("--noautoconnect", start.ArgumentList);
        Assert.DoesNotContain("--gui", start.ArgumentList);
        Assert.Equal(start.WorkingDirectory, start.ArgumentList[1]); Assert.Contains("6743", start.ArgumentList);
        Assert.DoesNotContain("QT_PLUGIN_PATH", start.Environment.Keys); Assert.DoesNotContain("PYTHONPATH", start.Environment.Keys);
        Assert.Equal(Environment.SystemDirectory, start.Environment["PATH"]);
        var control = new OpenRgbStartupProfile(OpenRgbStartupKind.AsrockPolychromeUsb, 6743).CreateStartInfo(Path.Combine(d.Path, "runtime"), Path.Combine(d.Path, "config"));
        Assert.DoesNotContain("--nodetect", control.ArgumentList);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MutableConfigurationCannotShareTheImmutableRuntimeTree(int relation)
    {
        using var d = new ServiceTestDirectory(); var root = Path.Combine(d.Path, "runtime");
        var config = relation == 0 ? root : relation == 1 ? Path.Combine(root, "config") : d.Path;
        Assert.Throws<IOException>(() => new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, 6742).CreateStartInfo(root, config));
    }
    [Theory] [InlineData(0)] [InlineData(1023)] [InlineData(65536)]
    public void InvalidPortsAreRejected(int port) => Assert.Throws<ArgumentException>(() => new OpenRgbStartupProfile(OpenRgbStartupKind.NoDetection, port));
}
