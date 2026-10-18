using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class DashboardTests
{
    private static BoardSnapshot Board(TestClock clock) => new("ASRock X870E Taichi White",
    [new("temp1", "CPU", "Nuvoton NCT6799D", "temperature", "°C", 42, 42),
     new("temp2", "Auxiliary", "Nuvoton NCT6799D", "temperature", "°C", 45, 45),
     new("/lpc/nct6799d/0/fan/1", "Fan #2", "Nuvoton NCT6799D", "fan", "rpm", 1361, 1361),
     new("pump-candidate", "CPU Fan", "Nuvoton NCT6686D", "fan", "rpm", 0, 0)], clock.GetTimestamp());
    [Fact] public async Task OldSettingsMigrateToUnifiedMonitoringWithoutControlSetup()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"cpuSensorId\":\"demo/cpu\",\"gpuSensorId\":\"demo/gpu\"}", Contract.Json)!;
        Assert.Null(old.Dashboard.Items);
        await using var f = new Fixture(old); await f.Initialize();
        var board = Board(f.Clock);
        var readings = DashboardDisplay.Read(old, f.Core.Snapshot, board, new(Reading: new(68.6, 1361, f.Clock.GetTimestamp())), f.Clock);
        Assert.Equal(4, readings.Count(r => r.Item.Kind == "temperature"));
        Assert.Contains(readings, r => r.Item.Source == "nzxt" && r.Value > 0);
        var cpu = Assert.Single(readings, r => r.Item.SensorId == "/lpc/nct6799d/0/fan/1");
        Assert.Equal(1361, cpu.Value); Assert.Equal(68.6, cpu.Duty); Assert.Equal("マザー管理", cpu.Control);
        Assert.DoesNotContain(readings, r => r.Item.SensorId == "pump-candidate");
        Assert.DoesNotContain(readings, r => r.Role == "ポンプ"); Assert.Equal(0, f.Hub.Writes);
    }
    [Fact] public void SavedMissingSelectionsKeepIdentityRoleAndOrderAndNewSensorsStayHidden()
    {
        var settings = new DashboardSettings([new("motherboard", "gone", "fan", "水冷ポンプ", true, "pump"), new("cpu", "selected", "temperature", "CPU", false)]);
        var items = DashboardDisplay.Items(Contract.Copy(settings), [new("motherboard", "new", "fan", "Fan")]);
        Assert.Equal("gone", items[0].SensorId); Assert.Equal("pump", items[0].Role); Assert.False(items[1].Visible); Assert.False(items[2].Visible);
    }
    [Fact] public async Task RenameReorderAndHideCannotChangeFanPolicyOrMotherboardSettings()
    {
        await using var f = new Fixture(); await f.Initialize(); await f.Manual(54);
        var before = f.Core.Settings; var snapshot = f.Core.Snapshot; var writes = f.Hub.Writes;
        var items = DashboardDisplay.Catalog(before, snapshot, Board(f.Clock)).Reverse().Select(i => i with { Name = "改名 " + i.Name, Visible = false }).ToArray();
        f.Core.UpdateSettings(before with { Dashboard = new(items) }, false);
        Assert.Equal(snapshot.ControlRevision, f.Core.Snapshot.ControlRevision); Assert.Equal(54, f.Core.Snapshot.ConfirmedDuty);
        Assert.Equal(writes, f.Hub.Writes); Assert.Equal(before.Motherboard, f.Core.Settings.Motherboard); Assert.Equal(before.CpuFan, f.Core.Settings.CpuFan);
        Assert.Equal(items, f.Core.Settings.Dashboard.Items);
    }
    [Fact] public async Task ZeroMissingAndStaleAreDistinctAndOldDutyIsHidden()
    {
        await using var f = new Fixture(); await f.Initialize(); var board = Board(f.Clock);
        var settings = f.Core.Settings with { Dashboard = new([new("motherboard", "pump-candidate", "fan", "ポンプ", true, "pump"), new("motherboard", "missing", "fan", "未取得ファン"), new("nzxt", "fan1", "fan", "Hub")]) };
        var rows = DashboardDisplay.Read(settings, f.Core.Snapshot, board, new(), f.Clock);
        Assert.Equal("0 RPM", rows[0].State); Assert.Equal(0, rows[0].Value); Assert.Null(rows[0].Duty);
        Assert.Equal("未取得", rows[1].State); Assert.Null(rows[1].Value);
        var snapshot = f.Core.Snapshot; f.Clock.Advance(3);
        rows = DashboardDisplay.Read(settings, snapshot, board, new(), f.Clock);
        Assert.Equal("更新停止", rows[0].State); Assert.Equal("更新停止", rows[2].State);
        Assert.All(rows, r => { Assert.Null(r.Value); Assert.Null(r.Duty); });
    }
    [Fact] public async Task HubLossDoesNotInvalidateMotherboardOrCpuTemperature()
    {
        await using var f = new Fixture(); await f.Initialize();
        var rows = DashboardDisplay.Read(f.Core.Settings, f.Core.Snapshot with { HubReady = false }, Board(f.Clock), new(), f.Clock);
        Assert.All(rows.Where(r => r.Item.Source == "nzxt"), r => { Assert.Equal("未接続", r.State); Assert.Null(r.Value); });
        Assert.Contains(rows, r => r.Item.Source == "motherboard" && r.Value == 1361);
        Assert.Contains(rows, r => r.Item.Source == "cpu" && r.Value is not null);
    }
    [Fact] public async Task BoardFailureAndInvalidValuesNeverAppearAsLiveMeasurements()
    {
        await using var f = new Fixture(); await f.Initialize(); var board = Board(f.Clock);
        var broken = board with { Error = "read failed" };
        var rows = DashboardDisplay.Read(f.Core.Settings, f.Core.Snapshot, broken, new(), f.Clock);
        Assert.All(rows.Where(r => r.Item.Source == "motherboard"), r => Assert.Null(r.Value));
        broken = board with { Sensors = [board.Sensors[0] with { Value = double.NaN }] };
        rows = DashboardDisplay.Read(f.Core.Settings, f.Core.Snapshot, broken, new(), f.Clock);
        Assert.Null(Assert.Single(rows, r => r.Item.Source == "motherboard").Value);
        Assert.False(DashboardDisplay.Fresh(f.Clock.GetTimestamp() + 1000, f.Clock));
    }
    [Fact] public async Task ArbitraryFanNameCannotBecomeCpuFanControlAndKnownDutyExpires()
    {
        await using var f = new Fixture(); await f.Initialize(); var board = Board(f.Clock);
        var settings = f.Core.Settings with { Dashboard = new([new("motherboard", "pump-candidate", "fan", "CPU_FAN1")]) };
        var cpu = new CpuFanState("manual", 70, new(70, 1400, f.Clock.GetTimestamp()), true);
        var row = Assert.Single(DashboardDisplay.Read(settings, f.Core.Snapshot, board, cpu, f.Clock));
        Assert.Null(row.Duty); Assert.Equal("監視のみ", row.Control);
        f.Clock.Advance(3); board = board with { Timestamp = f.Clock.GetTimestamp() };
        row = Assert.Single(DashboardDisplay.Read(f.Core.Settings, f.Core.Snapshot, board, cpu, f.Clock), r => r.Item.SensorId == "/lpc/nct6799d/0/fan/1");
        Assert.Equal(1361, row.Value); Assert.Null(row.Duty);
    }
    [Theory] [InlineData("bad")] [InlineData("duplicate")] [InlineData("role")] [InlineData("kind")] [InlineData("null")]
    public void InvalidSettingsAreRejected(string variant)
    {
        var item = new DashboardItem("cpu", "selected", "temperature", "CPU");
        var settings = variant switch
        {
            "bad" => new DashboardSettings([item with { Name = "\n" }]),
            "duplicate" => new([item, item]), "role" => new([item with { Role = "controller" }]),
            "kind" => new([item with { Kind = "fan" }]), _ => null
        };
        Assert.NotNull(DashboardDisplay.Validate(settings));
    }
    [Fact] public void HistoryIsBoundedLeavesGapsAndResetsWhenSelectedSensorChanges()
    {
        var clock = new TestClock(); var history = new DashboardHistory(clock);
        var reading = new DashboardReading(new("cpu", "selected", "temperature", "CPU"), 50, null, "取得中", "監視のみ", "CPU", "cpu:sensor1");
        for (var i = 0; i < 1000; i++) { history.Add([reading]); history.Add([reading]); clock.Advance(1); }
        Assert.InRange(history.Get(reading.Key).Length, 599, 601);
        history.Add([reading with { Value = null }]); Assert.Null(history.Get(reading.Key)[^1].Value);
        clock.Advance(1); history.Add([reading with { HistoryKey = "cpu:sensor2", Value = 60 }]);
        Assert.Equal(60, Assert.Single(history.Get(reading.Key)).Value);
        history.Add([]); Assert.Empty(history.Get(reading.Key));
    }
}
