namespace FlowDeck.Core;

public record CpuFanSettings(bool WiringConfirmed = false, int? Minimum = null, AutoProfile? Profile = null);
public record CpuFanReading(double Duty, int Rpm, long Timestamp);
public record CpuFanTest(CpuFanReading Before, CpuFanReading FullSpeed, bool Restored);
public record CpuFanState(string Mode = "monitor", int? Target = null, CpuFanReading? Reading = null,
    bool Managing = false, string Message = "マザーボード側の制御で動作しています", CpuFanTest? Test = null);
public interface ICpuFanDriver
{
    Task<CpuFanReading> ReadAsync(CancellationToken ct = default);
    Task<CpuFanReading> WriteAsync(int duty, CancellationToken ct = default);
    Task RestoreAsync(CancellationToken ct = default);
}

/// <summary>One independent motherboard fan owner, with its own restoration obligation.</summary>
public sealed class CpuFanController(ICpuFanDriver driver, Func<SensorReading[]> temperatures, IEventLog log,
    TimeProvider? time = null, string name = "CPU_FAN1") : IBoardFanController
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DescentLimiter descent = new();
    private CpuFanState state = new();
    private CpuFanSettings? active;
    private Task? loop;
    private long? zeroRpmSince;
    private bool suspended, closing;
    private string? readError;
    public CpuFanState State => Volatile.Read(ref state);
    public static string? Validate(CpuFanSettings? settings, string name = "CPU_FAN1")
    {
        if (settings is null) return $"{name}の設定が不正です";
        if (settings.Minimum is not null and not (>= 1 and <= 100)) return $"{name}の最低出力は1〜100%で入力してください";
        if (settings.Profile is { } p && FanPolicyEngine.ValidateProfile(p, Limits(settings)) is { } error) return error;
        return null;
    }
    private static FanLimits Limits(CpuFanSettings settings) => new(settings.Minimum, 100, 100);
    private void Set(CpuFanState next, bool record = false)
    {
        Volatile.Write(ref state, next);
        if (record) log.Write("motherboardFan", new { port = name, state = next });
    }
    private void RequireIdle()
    {
        if (closing || suspended) throw new InvalidOperationException("終了・スリープ処理中です");
        if (State.Mode == "fault") throw new InvalidOperationException("原因を確認し、「マザーボードへ制御を戻す」を実行してください");
    }
    private void RequireSettings(CpuFanSettings settings)
    {
        RequireIdle();
        if (Validate(settings, name) is { } error) throw new InvalidOperationException(error);
        if (!settings.WiringConfirmed) throw new InvalidOperationException($"{name}の接続と端子テストの結果を確認してください");
        if (settings.Minimum is null) throw new InvalidOperationException("このファンの確認済み最低出力を設定してください");
    }
    private async Task<CpuFanReading> Write(int duty)
    {
        // A failed/partially completed write still owns a restoration obligation.
        Set(State with { Managing = true, Target = duty });
        var reading = await driver.WriteAsync(duty, lifetime.Token);
        for (var i = 0; i < 10 && Math.Abs(reading.Duty - duty) > 1; i++)
        { await Task.Delay(100, lifetime.Token); reading = await driver.ReadAsync(lifetime.Token); }
        if (Math.Abs(reading.Duty - duty) > 1 || !Fresh(reading.Timestamp)) throw new IOException($"{name}の報告出力が要求と一致しません");
        Set(State with { Reading = reading });
        return reading;
    }
    public async Task ManualAsync(CpuFanSettings settings, int duty)
    {
        await gate.WaitAsync();
        try
        {
            RequireSettings(settings);
            if (duty < settings.Minimum || duty > 100) throw new InvalidOperationException("確認済み最低出力〜100%で指定してください");
            active = Contract.Copy(settings);
            try { await Write(duty); Set(State with { Mode = "manual", Message = $"{name} 手動 {duty}%・報告一致" }, true); }
            catch (Exception e) { await Fault(e.Message); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task AutoAsync(CpuFanSettings settings)
    {
        await gate.WaitAsync();
        try
        {
            RequireSettings(settings);
            if (settings.Profile is null) throw new InvalidOperationException($"{name}の温度カーブを保存してください");
            var saved = Contract.Copy(settings);
            var target = Calculate(saved, starting: true);
            active = saved;
            try
            {
                await Write(target); descent.Reset(target, clock.GetTimestamp());
                Set(State with { Mode = "auto", Message = $"{name} 温度カーブで制御中" }, true);
            }
            catch (Exception e) { await Fault(e.Message); throw; }
        }
        finally { gate.Release(); }
    }
    private int Calculate(CpuFanSettings settings, bool starting = false)
    {
        var values = temperatures();
        var selected = new Dictionary<string, double>();
        foreach (var curve in new[] { settings.Profile!.Cpu, settings.Profile.Gpu }.OfType<SensorCurve>())
        {
            var sensor = values.FirstOrDefault(s => s.Id == curve.SensorId);
            if (sensor?.Celsius is not { } value || !double.IsFinite(value) || !Fresh(sensor.Timestamp))
                throw new IOException($"{name}の制御温度が未取得または3秒以上古くなりました");
            if (value >= curve.Overheat) throw new IOException($"{name}の制御温度が過熱条件に達しました");
            if (starting && value >= curve.ResumeBelow) throw new IOException($"{name}の自動制御開始には、設定した開始・再開温度を下回る必要があります");
            selected[curve.SensorId] = value;
        }
        return FanPolicyEngine.Calculate(settings.Profile, Limits(settings), selected);
    }
    public async Task TestAsync()
    {
        await gate.WaitAsync();
        try
        {
            RequireIdle();
            if (State.Managing) throw new InvalidOperationException("先にマザーボードへ制御を戻してください");
            var before = await driver.ReadAsync(lifetime.Token);
            if (!Fresh(before.Timestamp) || before.Rpm <= 0) throw new IOException($"{name}候補の回転数を取得できません");
            Set(State with { Mode = "test", Test = null, Reading = before, Message = $"{name}候補を5秒間100%にします。対象ファンの変化を確認してください" }, true);
            CpuFanReading? full = null;
            Exception? failure = null;
            try
            {
                await Write(100); await Task.Delay(TimeSpan.FromSeconds(5), clock, lifetime.Token); full = await driver.ReadAsync(lifetime.Token);
                if (!Fresh(full.Timestamp) || full.Rpm <= 0 || Math.Abs(full.Duty - 100) > 1) throw new IOException("端子テスト中の100%出力・回転数を確認できません");
            }
            catch (Exception e) { failure = e; }
            try
            {
                await driver.RestoreAsync();
                var result = full is null ? null : new CpuFanTest(before, full, true);
                Set(new(Reading: await ReadAfterRestore(), Test: result, Message: failure is null
                    ? $"テスト完了：{before.Rpm} → {full!.Rpm} rpm。元の制御を復元しました。実際の{name}の変化を確認してください"
                    : "テスト中断・元の制御を復元しました: " + failure.Message), true);
            }
            catch (Exception e) { Set(State with { Mode = "fault", Managing = true, Message = "元の制御の復元を確認できません: " + e.Message }, true); throw; }
            if (failure is not null) throw new IOException(failure.Message, failure);
        }
        finally { gate.Release(); }
    }
    public async Task TickAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (suspended || closing) return;
            var reading = await driver.ReadAsync(lifetime.Token);
            var first = State.Reading is null;
            Set(State with { Reading = reading, Message = readError is not null && State.Mode == "monitor" ? "マザーボード側の制御で動作しています" : State.Message });
            readError = null;
            if (first) log.Write("motherboardFanInventory", new { port = name, reading });
            if (State.Mode is not ("manual" or "auto")) return;
            if (!Fresh(reading.Timestamp)) throw new IOException($"{name}の読み取りが古くなりました");
            if (reading.Rpm <= 0) zeroRpmSince ??= clock.GetTimestamp(); else zeroRpmSince = null;
            if (zeroRpmSince is { } stopped && clock.GetElapsedTime(stopped).TotalSeconds >= 5) throw new IOException($"{name}の回転数が5秒以上0です");
            if (State.Target is { } expected && Math.Abs(reading.Duty - expected) > 1) throw new IOException($"{name}の出力が別の制御で変更されました");
            if (State.Mode == "auto")
            {
                var target = descent.Next(Calculate(active!), clock.GetTimestamp(), active!.Profile!.DescentDelaySeconds!.Value,
                    active.Profile.DescentPerSecond!.Value, clock);
                if (target != State.Target) await Write(target);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (State.Managing && State.Mode != "fault") await Fault(e.Message);
            else
            {
                if (readError != e.Message) log.Write("cpuFanReadError", new { port = name, error = e.Message });
                readError = e.Message; Set(State with { Reading = null, Message = e.Message });
            }
        }
        finally { gate.Release(); }
    }
    private bool Fresh(long timestamp) => clock.GetElapsedTime(timestamp).TotalSeconds is >= 0 and < 3;
    private async Task Fault(string reason)
    {
        Set(State with { Mode = "fault", Message = reason }, true);
        try { await Write(100); Set(State with { Message = reason + "。100%へ変更し、自動復帰を停止しました" }, true); }
        catch (Exception e) { Set(State with { Reading = null, Message = reason + "。100%も確認できません: " + e.Message }, true); }
    }
    public async Task RestoreAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!State.Managing) { active = null; return; }
            try { await driver.RestoreAsync(); active = null; zeroRpmSince = null; Set(new(Reading: await ReadAfterRestore(), Test: State.Test), true); }
            catch (Exception e) { Set(State with { Mode = "fault", Message = "元の制御の復元を確認できません: " + e.Message }, true); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task SuspendAsync() { suspended = true; await RestoreAsync(); }
    private async Task<CpuFanReading?> ReadAfterRestore()
    {
        try { return await driver.ReadAsync(); }
        catch (Exception e) { log.Write("cpuFanReadAfterRestore", new { port = name, error = e.Message }); return null; }
    }
    public void Resume() { suspended = false; }
    public void Start() => loop ??= Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try { do { await TickAsync(); } while (await timer.WaitForNextTickAsync(lifetime.Token)); }
        catch (OperationCanceledException) { }
    });
    public async ValueTask DisposeAsync()
    {
        closing = true; await lifetime.CancelAsync();
        if (loop is not null) await loop;
        await RestoreAsync(); lifetime.Dispose();
    }
}
