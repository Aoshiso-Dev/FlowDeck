using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using LibreHardwareMonitor.Hardware;

namespace FlowDeck.Tests;

public partial class LhmSensorFreshnessTests
{
    private sealed class NvidiaReadings
    {
        private static readonly Assembly Library = typeof(Computer).Assembly;
        private static Type Kind(string name) => Library.GetType(name, true)!;
        private static Type Native(string name) => Kind("LibreHardwareMonitor.Interop.NvApi+" + name);
        private static readonly Type GpuType = Kind("LibreHardwareMonitor.Hardware.Gpu.NvidiaGpu");
        private static readonly Type HelperType = Kind("LibreHardwareMonitor.Hardware.Gpu.NvidiaSensorReadings");
        private readonly object gpu = RuntimeHelpers.GetUninitializedObject(GpuType);
        public readonly CachedHardware Hardware = new(HardwareType.GpuNvidia);
        public readonly ISensor[] Temperatures;
        public readonly ISensor Core, HotSpot, Memory;
        public ISensor[] Fans = [];
        public static void SetField(object instance, string name, object? value) => instance.GetType()
            .GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
        private static object Status(bool ok) => Enum.ToObject(Native("NvStatus"), ok ? 0 : -1);
        public static object Thermal(params (int Target, int Controller, int Temperature)[] entries)
        {
            var result = Activator.CreateInstance(Native("NvThermalSettings"))!;
            var array = Array.CreateInstance(Native("NvSensor"), Math.Max(entries.Length, 3));
            for (var i = 0; i < entries.Length; i++)
            {
                var item = Activator.CreateInstance(Native("NvSensor"))!;
                SetField(item, "Target", Enum.ToObject(Native("NvThermalTarget"), entries[i].Target));
                SetField(item, "Controller", Enum.ToObject(Native("NvThermalController"), entries[i].Controller));
                SetField(item, "CurrentTemp", entries[i].Temperature); array.SetValue(item, i);
            }
            SetField(result, "Count", (uint)entries.Length); SetField(result, "Sensor", array); return result;
        }
        public static object Additional(params (int Index, float Temperature)[] entries)
        {
            var result = Activator.CreateInstance(Native("NvThermalSensors"))!; var values = new int[32];
            foreach (var entry in entries) values[entry.Index] = (int)(entry.Temperature * 256);
            SetField(result, "Temperatures", values); return result;
        }
        public static object FanData(params (uint Id, uint Rpm)[] entries)
        {
            var result = Activator.CreateInstance(Native("NvFanCoolersStatus"))!;
            var array = Array.CreateInstance(Native("NvFanCoolersStatusItem"), Math.Max(entries.Length, 32));
            for (var i = 0; i < entries.Length; i++)
            {
                var item = Activator.CreateInstance(Native("NvFanCoolersStatusItem"))!;
                SetField(item, "CoolerId", entries[i].Id); SetField(item, "CurrentRpm", entries[i].Rpm); array.SetValue(item, i);
            }
            SetField(result, "Count", (uint)entries.Length); SetField(result, "Items", array); return result;
        }
        private ISensor MakeSensor(int index, SensorType type = SensorType.Temperature)
        {
            var result = (ISensor)Activator.CreateInstance(Kind("LibreHardwareMonitor.Hardware.Sensor"),
                ["Test " + type, index, false, type, Hardware, Array.Empty<ParameterDescription>(), new Settings(), false])!;
            Hardware.Add(result); return result;
        }
        public NvidiaReadings(object initial, string model = "NVIDIA GeForce RTX 5090", bool initialOk = true)
        {
            // Avoid NvidiaGpu's native constructor entirely; exercise the compiled application of API replies.
            typeof(Hardware).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(gpu, new HashSet<ISensor>());
            typeof(Hardware).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(gpu, model);
            Rename("Custom GPU name");
            var bindings = (Array)HelperType.GetMethod("ThermalBindings")!.Invoke(null, [initial, Status(initialOk)])!;
            var temperatures = Array.CreateInstance(Kind("LibreHardwareMonitor.Hardware.Sensor"), bindings.Length);
            Temperatures = new ISensor[bindings.Length];
            for (var i = 0; i < bindings.Length; i++) { Temperatures[i] = MakeSensor(i); temperatures.SetValue(Temperatures[i], i); }
            SetField(gpu, "_temperatures", temperatures); SetField(gpu, "_temperatureBindings", bindings);
            int coreIndex = (int)HelperType.GetMethod("CoreIndex")!.Invoke(null, [bindings])!;
            Core = coreIndex >= 0 ? Temperatures[coreIndex] : MakeSensor(bindings.Length);
            HotSpot = MakeSensor(bindings.Length + 1); Memory = MakeSensor(bindings.Length + 2);
            SetField(gpu, "_thermalCoreTemperature", Core); SetField(gpu, "_hotSpotTemperature", HotSpot); SetField(gpu, "_memoryJunctionTemperature", Memory);
        }
        public void Rename(string name) => typeof(Hardware).GetField("_customName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(gpu, name);
        public void SetFans(bool tachometer, params int[] ids)
        {
            var array = Array.CreateInstance(Kind("LibreHardwareMonitor.Hardware.Sensor"), ids.Length);
            Fans = ids.Select(i => MakeSensor(i, SensorType.Fan)).ToArray();
            for (int i = 0; i < ids.Length; i++) array.SetValue(Fans[i], i);
            SetField(gpu, "_fans", array); SetField(gpu, "_fansUseTachometer", tachometer);
        }
        private void Call(string method, params object[] args) => GpuType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(gpu, args);
        public void ApplyThermal(object reply, bool ok = true) => Call("ApplyThermalSettings", reply, Status(ok));
        public void ApplyAdditional(object reply, bool ok = true) => Call("ApplyAdditionalTemperatures", reply, Status(ok));
        public void ApplyFans(object reply, bool ok = true, int tach = 900, bool tachOk = true) =>
            Call("ApplyFanReadings", reply, Status(ok), tach, Status(tachOk));
        public static int FanCount(object reply, bool ok = true) => ((Array)HelperType.GetMethod("FanItems")!.Invoke(null, [reply, Status(ok)])!).Length;
    }

    [Fact]
    public void NvidiaTemperaturesFollowTargetAndControllerWhenDriverOrderChanges()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal((1, 1, 60), (2, 2, 80), (8, 2, 40)));
        fixture.ApplyThermal(NvidiaReadings.Thermal((8, 2, 41), (1, 1, 61), (2, 2, 81)));
        Assert.Equal(new float?[] { 61, 81, 41 }, fixture.Temperatures.Select(t => t.Value));
        fixture.ApplyThermal(NvidiaReadings.Thermal((1, 1, 62), (2, 3, 82)));
        Assert.Equal(new float?[] { 62, null, null }, fixture.Temperatures.Select(t => t.Value));
    }

    [Fact]
    public void NvidiaCountBoundsPreventUnusedArrayEntriesFromBecomingMeasurements()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal((1, 1, 60), (2, 1, 80)));
        var partial = NvidiaReadings.Thermal((1, 1, 65), (2, 1, 120)); NvidiaReadings.SetField(partial, "Count", 1U);
        fixture.ApplyThermal(partial); Assert.Equal(65, fixture.Temperatures[0].Value); Assert.Null(fixture.Temperatures[1].Value);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("zeroCount")]
    [InlineData("largeCount")]
    [InlineData("shortArray")]
    [InlineData("missingArray")]
    public void InvalidNvidiaThermalRepliesInvalidateOldValuesWithoutThrowing(string failure)
    {
        var reply = NvidiaReadings.Thermal((1, 1, 60)); var fixture = new NvidiaReadings(reply); fixture.ApplyThermal(reply);
        Assert.Equal(60, fixture.Temperatures[0].Value);
        switch (failure)
        {
            case "zeroCount": NvidiaReadings.SetField(reply, "Count", 0U); break;
            case "largeCount": NvidiaReadings.SetField(reply, "Count", uint.MaxValue); break;
            case "shortArray": NvidiaReadings.SetField(reply, "Sensor", Array.CreateInstance(reply.GetType().GetField("Sensor")!.FieldType.GetElementType()!, 0)); break;
            case "missingArray": NvidiaReadings.SetField(reply, "Sensor", null); break;
        }
        fixture.ApplyThermal(reply, failure != "failure"); Assert.Null(fixture.Temperatures[0].Value);
        var initial = new NvidiaReadings(reply, initialOk: failure != "failure"); Assert.Empty(initial.Temperatures);
    }

    [Fact]
    public void DuplicateNvidiaThermalIdentitiesDoNotChooseTheFirstMatch()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal((1, 1, 60), (2, 1, 80)));
        fixture.ApplyThermal(NvidiaReadings.Thermal((1, 1, 61), (1, 1, 62), (2, 1, 81)));
        Assert.Null(fixture.Temperatures[0].Value); Assert.Equal(81, fixture.Temperatures[1].Value);
        var ambiguous = new NvidiaReadings(NvidiaReadings.Thermal((1, 1, 60), (1, 1, 61)));
        ambiguous.ApplyThermal(NvidiaReadings.Thermal((1, 1, 62))); Assert.All(ambiguous.Temperatures, t => Assert.Null(t.Value));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(15)] [InlineData(123)]
    public void UnknownOrAggregateNvidiaTargetsAreNotAConcreteTemperature(int target)
    {
        var reply = NvidiaReadings.Thermal((target, 1, 60)); var fixture = new NvidiaReadings(reply);
        fixture.ApplyThermal(reply); Assert.Null(fixture.Temperatures[0].Value);
    }

    [Theory]
    [InlineData(-20)] [InlineData(0)] [InlineData(150)] [InlineData(int.MaxValue)]
    public void NvidiaVersionTwoUsesSignedTemperaturesAndRejectsUnusableValues(int value)
    {
        var reply = NvidiaReadings.Thermal((1, 1, value)); var fixture = new NvidiaReadings(reply);
        fixture.ApplyThermal(reply); Assert.Null(fixture.Temperatures[0].Value);
        var type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvApi+NvSensor")!;
        Assert.Equal(typeof(int), type.GetField("CurrentTemp")!.FieldType); Assert.Equal(20, Marshal.SizeOf(type));
    }

    [Fact]
    public void Rtx50AdditionalCoreReadingDoesNotOverwriteTheFirstNonCoreSensor()
    {
        var initial = NvidiaReadings.Thermal((2, 1, 80), (1, 1, 60)); var fixture = new NvidiaReadings(initial);
        fixture.ApplyThermal(initial); fixture.ApplyAdditional(NvidiaReadings.Additional((1, 65.5f), (2, 85.25f)));
        Assert.Equal(80, fixture.Temperatures[0].Value); Assert.Equal(65.5f, fixture.Temperatures[1].Value);
        Assert.Equal(85.25f, fixture.Memory.Value); Assert.Null(fixture.HotSpot.Value);
    }

    [Fact]
    public void Rtx50CanExposeItsAdditionalCoreWhenTheStandardListIsInitiallyMissing()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal());
        fixture.ApplyAdditional(NvidiaReadings.Additional((1, 55), (2, 75)));
        Assert.Empty(fixture.Temperatures); Assert.Equal(55, fixture.Core.Value); Assert.Equal(75, fixture.Memory.Value);
        fixture.ApplyAdditional(NvidiaReadings.Additional(), false); Assert.Null(fixture.Core.Value); Assert.Null(fixture.Memory.Value);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", 2, false)]
    [InlineData("NVIDIA GeForce RTX 4090", 7, true)]
    [InlineData("NVIDIA GeForce RTX 3090", 9, true)]
    public void AUserRenameCannotChangeThePinnedAdditionalTemperatureLayout(string model, int memorySlot, bool hotspot)
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal((1, 1, 60)), model);
        fixture.Rename("NVIDIA GeForce RTX 5090 custom name");
        fixture.ApplyAdditional(NvidiaReadings.Additional((1, 65), (2, 72), (7, 77), (9, 79)));
        Assert.Equal(memorySlot == 2 ? 72 : memorySlot == 7 ? 77 : 79, fixture.Memory.Value);
        Assert.Equal(hotspot ? 65f : null, fixture.HotSpot.Value);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("shortArray")]
    [InlineData("missingArray")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("tooHot")]
    public void InvalidAdditionalReadingsDoNotEraseAValidStandardCore(string failure)
    {
        var initial = NvidiaReadings.Thermal((1, 1, 60)); var fixture = new NvidiaReadings(initial); fixture.ApplyThermal(initial);
        fixture.ApplyAdditional(NvidiaReadings.Additional((1, 65), (2, 75)));
        fixture.ApplyThermal(initial); var extra = NvidiaReadings.Additional((1, 66), (2, 76));
        if (failure == "shortArray") NvidiaReadings.SetField(extra, "Temperatures", new int[3]);
        else if (failure == "missingArray") NvidiaReadings.SetField(extra, "Temperatures", null);
        else if (failure != "failure")
        {
            var bad = failure == "zero" ? 0 : failure == "negative" ? -10 : 150;
            extra = NvidiaReadings.Additional((1, bad), (2, bad));
        }
        fixture.ApplyAdditional(extra, failure != "failure"); Assert.Equal(60, fixture.Core.Value); Assert.Null(fixture.Memory.Value);
    }

    [Fact]
    public void NvidiaFanRpmFollowsCoolerIdAcrossReorderingRemovalAndUnknownAdditions()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal()); fixture.SetFans(false, 7, 2);
        fixture.ApplyFans(NvidiaReadings.FanData((2, 1200), (7, 900)));
        Assert.Equal(new float?[] { 900, 1200 }, fixture.Fans.Select(f => f.Value));
        fixture.ApplyFans(NvidiaReadings.FanData((40, 3000), (2, 0)));
        Assert.Null(fixture.Fans[0].Value); Assert.Equal(0, fixture.Fans[1].Value);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("zeroCount")]
    [InlineData("largeCount")]
    [InlineData("shortArray")]
    [InlineData("missingArray")]
    [InlineData("duplicate")]
    [InlineData("largeId")]
    public void InvalidCoolerReplyCannotSubstituteAnUnidentifiedTachometerReading(string failure)
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal()); fixture.SetFans(false, 7);
        var reply = NvidiaReadings.FanData((7, 900)); fixture.ApplyFans(reply); Assert.Equal(900, fixture.Fans[0].Value);
        switch (failure)
        {
            case "zeroCount": NvidiaReadings.SetField(reply, "Count", 0U); break;
            case "largeCount": NvidiaReadings.SetField(reply, "Count", uint.MaxValue); break;
            case "shortArray": NvidiaReadings.SetField(reply, "Items", Array.CreateInstance(reply.GetType().GetField("Items", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType.GetElementType()!, 0)); break;
            case "missingArray": NvidiaReadings.SetField(reply, "Items", null); break;
            case "duplicate": reply = NvidiaReadings.FanData((7, 900), (7, 2000)); break;
            case "largeId": reply = NvidiaReadings.FanData((uint.MaxValue, 900)); break;
        }
        fixture.ApplyFans(reply, failure != "failure", tach: 2000); Assert.Null(fixture.Fans[0].Value);
        Assert.Equal(0, NvidiaReadings.FanCount(reply, failure != "failure"));
    }

    [Fact]
    public void LegacyTachometerDoesNotRebindItselfToANewCoolerList()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal()); fixture.SetFans(true, 1);
        fixture.ApplyFans(NvidiaReadings.FanData((1, 2000)), tach: 0); Assert.Equal(0, fixture.Fans[0].Value);
        fixture.ApplyFans(NvidiaReadings.FanData((1, 2000)), tachOk: false); Assert.Null(fixture.Fans[0].Value);
        fixture.ApplyFans(NvidiaReadings.FanData((1, 2000)), tach: -1); Assert.Null(fixture.Fans[0].Value);
    }

    [Fact]
    public void WrongNvidiaTargetBecomesUnavailableInFlowDeckAndRecoversOnTheCorrectTarget()
    {
        var fixture = new NvidiaReadings(NvidiaReadings.Thermal((1, 1, 60))); var freshness = new LhmSensorFreshness();
        HardwareSensorValue ReadCore(object reply, long time)
        {
            fixture.Hardware.OnUpdate = () => fixture.ApplyThermal(reply);
            var acquired = freshness.Update(fixture.Hardware, time, out _); var values = new List<HardwareSensorValue>();
            HardwareSensorCapture.AddValues(fixture.Hardware, values, acquired);
            return values.Single(s => s.Id == fixture.Core.Identifier.ToString());
        }
        Assert.Equal(60, ReadCore(NvidiaReadings.Thermal((1, 1, 60)), 1000).Value);
        var missing = ReadCore(NvidiaReadings.Thermal((2, 1, 30)), 2000); Assert.Null(missing.Value); Assert.NotNull(missing.Problem);
        var recovered = ReadCore(NvidiaReadings.Thermal((1, 1, 70)), 3000); Assert.Equal(70, recovered.Value); Assert.Null(recovered.Problem);
    }
}
