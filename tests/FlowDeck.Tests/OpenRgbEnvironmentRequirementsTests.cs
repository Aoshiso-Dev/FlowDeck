using System.Text.Json;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class OpenRgbEnvironmentRequirementsTests
{
    private const string Detectors = "{\"hid_safe_mode\":true,\"detectors\":{\"Direct-owned\":false,\"SDK-owned\":true}}";
    private const string Drivers = "{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2}";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static OpenRgbEnvironmentRequirements Rules() => new("fixture-rules-v1", new('a', 40), [new("Direct-owned", false), new("SDK-owned", true)], true, true, 2);
    private static OpenRgbSdkEnvironment Environment(string detectors = Detectors, string drivers = Drivers) => new(
        new(Guid.NewGuid(), Guid.NewGuid(), 7, new('b', 64)), 25,
        Json(detectors), Json("{\"hid_safe_mode\":{\"type\":\"bool\",\"title\":\"Any localized title\"}}"), Json(drivers),
        Json("{\"all_controllers\":false,\"default_host\":\"configured-default-is-not-listener-proof\"}"), []);

    [Fact]
    public void MatchesSettingsReportsItsFixedRuleAndObservationWithoutCertifyingBusOwnership()
    {
        var rules = Rules(); var value = Environment(); var result = rules.Assess(value);
        Assert.True(result.MatchesSettings); Assert.Empty(result.Issues); Assert.False(result.Truncated);
        Assert.Equal("fixture-rules-v1", result.RuleId); Assert.Equal(new string('a', 40), result.SourceRevision); Assert.Equal(value.Stamp, result.Stamp);
        rules.Check(value);
    }

    [Fact]
    public void MissingDetectorCannotBeInterpretedAsDisabled()
    {
        var result = Rules().Assess(Environment("{\"hid_safe_mode\":true,\"detectors\":{\"SDK-owned\":true}}"));
        var issue = Assert.Single(result.Issues); Assert.Equal("missing-setting", issue.Code); Assert.Equal("Direct-owned", issue.Subject);
        Assert.False(result.MatchesSettings);
    }

    [Theory]
    [InlineData("true", "setting-mismatch")]
    [InlineData("null", "invalid-setting")]
    [InlineData("0", "invalid-setting")]
    [InlineData("\"false\"", "invalid-setting")]
    public void ConflictingOrNonBooleanDetectorValuesAreRejected(string value, string code)
    {
        var result = Rules().Assess(Environment(Detectors.Replace("\"Direct-owned\":false", "\"Direct-owned\":" + value)));
        Assert.Equal(code, Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void ANewDetectorChangesTheAuditedSetEvenWhenDisabled(string enabled)
    {
        var value = Detectors.Replace("\"SDK-owned\":true", "\"SDK-owned\":true,\"New detector\":" + enabled);
        var issue = Assert.Single(Rules().Assess(Environment(value)).Issues);
        Assert.Equal("unknown-detector", issue.Code); Assert.Equal("New detector", issue.Subject);
    }

    [Fact]
    public void RenamedOrCaseChangedDetectorIsNotSilentlyMatched()
    {
        var result = Rules().Assess(Environment(Detectors.Replace("SDK-owned", "sdk-owned")));
        Assert.Contains(result.Issues, i => i.Code == "missing-setting"); Assert.Contains(result.Issues, i => i.Code == "unknown-detector");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void MissingDriverSettingsNeverAcquireSourceDefaults(string drivers)
    {
        var result = Rules().Assess(Environment(drivers: drivers));
        Assert.Equal(2, result.Issues.Length); Assert.All(result.Issues, i => Assert.Equal("missing-setting", i.Code));
    }

    [Theory]
    [InlineData("{\"shared_smbus_access\":false,\"smbus_sleep_mode\":2}", "setting-mismatch")]
    [InlineData("{\"shared_smbus_access\":\"true\",\"smbus_sleep_mode\":2}", "invalid-setting")]
    [InlineData("{\"shared_smbus_access\":true,\"smbus_sleep_mode\":1}", "setting-mismatch")]
    [InlineData("{\"shared_smbus_access\":true,\"smbus_sleep_mode\":3}", "invalid-setting")]
    [InlineData("{\"shared_smbus_access\":true,\"smbus_sleep_mode\":2.5}", "invalid-setting")]
    public void DriverValuesRequireTheDeclaredTypeRangeAndExactPolicy(string drivers, string code)
    {
        Assert.Equal(code, Assert.Single(Rules().Assess(Environment(drivers: drivers)).Issues).Code);
    }

    [Fact]
    public void PluginUnavailabilityAndAnObservedPluginHaveDistinctReasons()
    {
        var value = Environment();
        Assert.Equal("plugins-unavailable", Assert.Single(Rules().Assess(value with { Plugins = null }).Issues).Code);
        Assert.Equal("plugins-unavailable", Assert.Single(Rules().Assess(value with { ServerFlags = 17 }).Issues).Code);
        Assert.Equal("plugins-present", Assert.Single(Rules().Assess(value with { Plugins = [new("Any effect", "private description", "1", 1, 1)] }).Issues).Code);
    }

    [Fact]
    public void ExternalControllersMissingSchemaAndUnexpectedPrivilegesAreSeparateFailures()
    {
        var value = Environment() with { Server = Json("{\"all_controllers\":true}"), DetectorSchema = Json("{}"), ServerFlags = 65561 };
        var result = Rules().Assess(value);
        Assert.Contains(result.Issues, i => i.Code == "schema-mismatch"); Assert.Contains(result.Issues, i => i.Code == "sdk-flags");
        Assert.Contains(result.Issues, i => i.Setting == "Server.all_controllers");
    }

    [Fact]
    public void NoDetectorObjectIsDifferentFromAnEmptyDetectorObject()
    {
        var unavailable = Rules().Assess(Environment("{\"hid_safe_mode\":true}"));
        Assert.Equal("detectors-unavailable", Assert.Single(unavailable.Issues).Code);
        var missing = Rules().Assess(Environment("{\"hid_safe_mode\":true,\"detectors\":{}}"));
        Assert.Equal(2, missing.Issues.Length); Assert.All(missing.Issues, i => Assert.Equal("missing-setting", i.Code));
    }

    [Fact]
    public void RequirementCollectionIsFrozenBeforeLaterCallerChanges()
    {
        var values = new[] { new OpenRgbDetectorRequirement("Direct-owned", false), new OpenRgbDetectorRequirement("SDK-owned", true) };
        var rules = new OpenRgbEnvironmentRequirements("fixture", new('a', 40), values, true, true, 2);
        values[0] = values[0] with { Enabled = true };
        Assert.True(rules.Assess(Environment()).MatchesSettings);
    }

    [Fact]
    public void InvalidAmbiguousOrUnboundedCompiledRequirementsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("fixture", new('a', 40), [], true, true, 2));
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("fixture", new('a', 40), [new("A", true), new("A", false)], true, true, 2));
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("fixture", new('a', 40), [new("bad\nname", true)], true, true, 2));
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("../rule", new('a', 40), [new("A", true)], true, true, 2));
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("fixture", "unversioned", [new("A", true)], true, true, 2));
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("fixture", new('a', 40), [new("A", true)], true, true, 3));
        Assert.Throws<ArgumentException>(() => new OpenRgbEnvironmentRequirements("fixture", new('a', 40), Enumerable.Range(0, 8193).Select(i => new OpenRgbDetectorRequirement("d" + i, false)), true, true, 2));
    }

    [Fact]
    public void CaseDistinctUpstreamDetectorsKeepIndependentExpectedValues()
    {
        var rules = new OpenRgbEnvironmentRequirements("fixture", new('a', 40),
            [new("Gigabyte GeForce RTX 5090 GAMING OC", false), new("Gigabyte GeForce RTX 5090 Gaming OC", true)], true, true, 2);
        var correct = "{\"hid_safe_mode\":true,\"detectors\":{\"Gigabyte GeForce RTX 5090 GAMING OC\":false,\"Gigabyte GeForce RTX 5090 Gaming OC\":true}}";
        Assert.True(rules.Assess(Environment(correct)).MatchesSettings);
        var swapped = correct.Replace("GAMING OC\":false", "GAMING OC\":true").Replace("Gaming OC\":true", "Gaming OC\":false");
        Assert.Equal(2, rules.Assess(Environment(swapped)).Issues.Count(i => i.Code == "setting-mismatch"));
    }

    [Fact]
    public void AssessmentIsBoundedAndDoesNotExposeUnrelatedSettingsValues()
    {
        var unknown = Enumerable.Range(0, 100).ToDictionary(i => "Unknown " + i + new string('x', 300), _ => false);
        unknown.Add("Direct-owned", false); unknown.Add("SDK-owned", true);
        var value = Environment() with
        {
            Detectors = JsonSerializer.SerializeToElement(new { hid_safe_mode = true, detectors = unknown, arbitrary_secret = "secret-value" }),
            Drivers = Json("{\"shared_smbus_access\":\"secret-value\",\"smbus_sleep_mode\":2}")
        };
        var result = Rules().Assess(value); Assert.False(result.MatchesSettings); Assert.True(result.Truncated); Assert.Equal(64, result.Issues.Length);
        Assert.All(result.Issues, i => Assert.True(i.Subject.Length <= 160));
        Assert.DoesNotContain("secret-value", JsonSerializer.Serialize(result));
        var error = Assert.Throws<IOException>(() => Rules().Check(value)); Assert.DoesNotContain("secret-value", error.Message); Assert.True(error.Message.Length < 800);
    }
}
