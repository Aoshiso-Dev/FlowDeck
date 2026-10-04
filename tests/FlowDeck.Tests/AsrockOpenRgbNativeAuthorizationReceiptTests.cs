using System.Text;
using System.Text.Json;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbNativeAuthorizationReceiptTests
{
    private static JsonDocument Fixture()
    {
        using var stream = typeof(AsrockOpenRgbNativeAuthorizationReceiptTests).Assembly
            .GetManifestResourceStream("FlowDeck.Tests.AsrockNativeAuthorizationPatch5.json")!;
        return JsonDocument.Parse(stream);
    }

    public static IEnumerable<object[]> Cases()
    {
        using var fixture = Fixture();
        return fixture.RootElement.EnumerateArray().Select(e => new object[] { e.GetProperty("case").GetString()! }).ToArray();
    }

    [Theory, MemberData(nameof(Cases))]
    public void ActualNativeAuthorizationResultsKeepUnsentAndUncertainOperationsDistinct(string name)
    {
        // The JSON is emitted by the compiled patch-5 SDK parsers, RGB base, controller and
        // checked HID path. Only authorization decisions and OS collaborators are mocked.
        using var fixture = Fixture();
        var entry = fixture.RootElement.EnumerateArray().Single(e => e.GetProperty("case").GetString() == name);
        static AsrockOpenRgbOperationReceipt Read(JsonElement element) =>
            AsrockOpenRgbOperationReceipt.Parse(Encoding.UTF8.GetBytes(element.GetRawText() + "\0"));
        var before = Read(entry.GetProperty("before")); var after = Read(entry.GetProperty("after"));
        var ack = entry.GetProperty("ack").GetUInt32(); var command = entry.GetProperty("command").GetUInt32();
        AsrockOpenRgbOperationReceipt.CheckResult(before, after, command, ack);
        if (name.StartsWith("auth-peer-", StringComparison.Ordinal))
        {
            Assert.Equal(3u, ack); Assert.True(before.Same(after));
            return;
        }
        Assert.Equal(before.Sequence + 1, after.Sequence);
        Assert.True(before.After.Same(after.Before));
        if (name.EndsWith("-success", StringComparison.Ordinal))
        {
            Assert.Equal(0u, ack); Assert.Equal(AsrockOpenRgbOperationStage.Completed, after.Stage);
            Assert.True(after.After.Completed > before.After.Completed); Assert.False(after.After.Faulted);
            return;
        }
        Assert.Equal(1u, ack); Assert.Equal(AsrockOpenRgbOperationStage.Failed, after.Stage);
        Assert.Throws<IOException>(() => AsrockOpenRgbOperationReceipt.CheckResult(before, after, command, 0));
        if (name == "auth-partial-direct")
        {
            Assert.Equal(before.After.Completed + 2, after.After.Completed);
            Assert.Equal(after.After.Completed, after.After.Attempted);
            Assert.Equal(0xe4, after.After.Request[4]); Assert.False(after.After.Faulted);
        }
        else if (name.EndsWith("-read", StringComparison.Ordinal) || name.EndsWith("-reply", StringComparison.Ordinal))
        {
            Assert.Equal(before.After.Attempted + 1, after.After.Attempted);
            Assert.Equal(before.After.Completed, after.After.Completed);
            Assert.Equal(2, after.After.Stage); Assert.True(after.After.Faulted);
            Assert.Equal(65, after.After.WriteResult);
            Assert.Equal(name.EndsWith("-read", StringComparison.Ordinal) ? -2 : 64, after.After.ReadResult);
        }
        else Assert.True(before.After.Same(after.After));
    }
}
