using System.Text;
using System.Text.Json.Nodes;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class AsrockOpenRgbAuthorizationProtocolTests
{
    internal static JsonObject Request(int phase = 0)
    {
        var request = new int[65]; var response = new int[64];
        if (phase > 0) { request[1] = 0x14; request[3] = 1; }
        if (phase is 1 or 4) response[3] = 1;
        return new JsonObject
        {
            ["version"] = 1, ["sequence"] = 1UL, ["phase"] = phase, ["path"] = "fixture",
            ["origin"] = new JsonObject { ["sdk"] = false, ["protocol"] = 0, ["command"] = 0, ["controller"] = 0, ["serverPort"] = 0, ["peerPort"] = 0 },
            ["io"] = new JsonObject
            {
                ["attempted"] = phase == 0 ? 0 : 1, ["completed"] = phase == 1 ? 1 : 0,
                ["stage"] = phase switch { 0 => 0, 1 => 3, 2 => 1, _ => 2 }, ["faulted"] = false,
                ["writeResult"] = phase is 0 or 2 ? -2 : 65, ["readResult"] = phase is 1 or 4 ? 64 : -2,
                ["request"] = new JsonArray(request.Select(n => JsonValue.Create(n)!).ToArray()),
                ["response"] = new JsonArray(response.Select(n => JsonValue.Create(n)!).ToArray())
            }
        };
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void DecodesTransientIoWithoutCertifyingHardwarePermission(int phase)
    {
        var result = AsrockOpenRgbAuthorizationProtocol.Read(Encoding.UTF8.GetBytes(Request(phase).ToJsonString()));
        Assert.Equal((AsrockAuthorizationPhase)phase, result.Phase); Assert.Equal(1UL, result.Sequence);
        Assert.False(result.Io.Faulted); Assert.False(result.Origin.Sdk);
    }
    [Theory]
    [InlineData("version")] [InlineData("sequence-zero")] [InlineData("sequence-overflow")] [InlineData("phase")]
    [InlineData("missing")] [InlineData("extra")] [InlineData("duplicate")] [InlineData("negative")]
    [InlineData("float")] [InlineData("path-null")] [InlineData("path-space")] [InlineData("path-long")]
    [InlineData("sdk-mixed")] [InlineData("sdk-command")] [InlineData("sdk-port")] [InlineData("sdk-protocol")]
    [InlineData("io-fault")] [InlineData("io-gap")] [InlineData("io-stage")] [InlineData("io-write")] [InlineData("io-read")]
    [InlineData("request-length")] [InlineData("request-report")] [InlineData("request-flash")]
    [InlineData("response-length")] [InlineData("response-mismatch")] [InlineData("byte-overflow")]
    public void RejectsMalformedAndContradictoryBoundaryReports(string kind)
    {
        var data = Request(4); var source = data["origin"]!; var io = data["io"]!;
        switch (kind)
        {
            case "version": data["version"] = 2; break;
            case "sequence-zero": data["sequence"] = 0; break;
            case "sequence-overflow": data["sequence"] = ulong.MaxValue; break;
            case "phase": data["phase"] = 5; break;
            case "missing": data.Remove("path"); break;
            case "extra": data["allow"] = true; break;
            case "negative": data["sequence"] = -1; break;
            case "float": data["sequence"] = 1.5; break;
            case "path-null": data["path"] = null; break;
            case "path-space": data["path"] = "path with space"; break;
            case "path-long": data["path"] = new string('x', 2049); break;
            case "sdk-mixed": source["peerPort"] = 10000; break;
            case "sdk-command": source["sdk"] = true; source["command"] = 1102; break;
            case "sdk-port": source["peerPort"] = 65536; break;
            case "sdk-protocol": source["sdk"] = true; source["protocol"] = 5; break;
            case "io-fault": io["faulted"] = true; break;
            case "io-gap": io["attempted"] = 3; break;
            case "io-stage": io["stage"] = 3; break;
            case "io-write": io["writeResult"] = 64; break;
            case "io-read": io["readResult"] = -2; break;
            case "request-length": io["request"]!.AsArray().RemoveAt(0); break;
            case "request-report": io["request"]![0] = 1; break;
            case "request-flash": io["request"]![1] = 0x12; break;
            case "response-length": io["response"]!.AsArray().Add(0); break;
            case "response-mismatch": io["response"]![3] = 2; break;
            case "byte-overflow": io["response"]![63] = 256; break;
        }
        var json = data.ToJsonString(); if (kind == "duplicate") json = json.Insert(1, "\"phase\":4,");
        Assert.Throws<IOException>(() => AsrockOpenRgbAuthorizationProtocol.Read(Encoding.UTF8.GetBytes(json)));
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(-1)] [InlineData(16385)]
    public void RejectsFrameLengthsBeforeAllocatingBody(int length) =>
        Assert.Throws<IOException>(() => AsrockOpenRgbAuthorizationProtocol.Length(BitConverter.GetBytes(length), 16384));
}
