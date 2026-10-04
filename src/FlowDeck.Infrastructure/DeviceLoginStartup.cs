using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace FlowDeck.Infrastructure;

public enum DeviceLoginStartupState { Unregistered, Registered, OtherRegistration, Unavailable }
public sealed record DeviceLoginStartupSnapshot(string Revision, DeviceLoginStartupState State, string? Problem = null);
public sealed record DeviceLoginStartupValue(string Revision, string? Command)
{
    public static DeviceLoginStartupValue Missing { get; } = new("missing", null);
    public static DeviceLoginStartupValue From(RegistryValueKind kind, object value) => new(
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Kind = kind, Value = value }))),
        kind == RegistryValueKind.String ? value as string : null);
}
public interface IDeviceLoginStartupRegistration
{
    bool IsDemo { get; }
    DeviceLoginStartupValue Read();
    void Update(string expectedRevision, string? command);
    void OpenWindowsSettings();
}

/// <summary>The current user's OS registration is authoritative, never an imported service preference.</summary>
public sealed class DeviceLoginStartup(string executable, IDeviceLoginStartupRegistration registration, Func<string, bool>? exists = null)
{
    private readonly SemaphoreSlim access = new(1, 1);
    private readonly Func<string, bool> fileExists = exists ?? File.Exists;
    public string Command { get; } = BuildCommand(executable);
    public bool IsDemo => registration.IsDemo;
    public static string BuildCommand(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || executable.Any(c => char.IsControl(c) || c == '"') ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("この実行ファイルを自動起動へ登録できません");
        var command = "\"" + Path.GetFullPath(executable) + "\" --devices --tray";
        if (command.Length > 260) throw new ArgumentException("自動起動の登録には実行ファイルのパスが長すぎます");
        return command;
    }
    public static DeviceLoginStartup? ForCurrentProcess()
    {
        try { return string.Equals(Path.GetFileName(Environment.ProcessPath), "FlowDeck.exe", StringComparison.OrdinalIgnoreCase)
            ? new(Environment.ProcessPath!, new WindowsLoginStartupRegistration()) : null; }
        catch (ArgumentException) { return null; }
    }
    private bool MatchesCommand(string? value)
    {
        const string suffix = " --devices --tray";
        return value is not null && value.EndsWith(suffix, StringComparison.Ordinal) && value.Length == Command.Length &&
            value.AsSpan(0, value.Length - suffix.Length).Equals(Command.AsSpan(0, Command.Length - suffix.Length), StringComparison.OrdinalIgnoreCase);
    }
    private DeviceLoginStartupSnapshot Project(DeviceLoginStartupValue value) => new(value.Revision,
        value.Revision == DeviceLoginStartupValue.Missing.Revision ? DeviceLoginStartupState.Unregistered :
        MatchesCommand(value.Command) ? DeviceLoginStartupState.Registered : DeviceLoginStartupState.OtherRegistration);
    public async Task<DeviceLoginStartupSnapshot> ReadAsync()
    {
        await access.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Project(registration.Read())).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new("unavailable", DeviceLoginStartupState.Unavailable, "Windowsの自動起動登録を取得できません"); }
        finally { access.Release(); }
    }
    public async Task<DeviceLoginStartupSnapshot> SaveAsync(DeviceLoginStartupSnapshot expected, bool enabled, bool replaceOther = false)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await access.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var current = Project(registration.Read());
                if (current != expected || current.State == DeviceLoginStartupState.Unavailable) throw new IOException("Windowsの登録が変わりました。再取得してください");
                if (current.State == DeviceLoginStartupState.OtherRegistration && (!enabled || !replaceOther))
                    throw new IOException("別のFlowDeckが登録されています。このアプリへの変更を選んでください");
                if (enabled && !fileExists(Path.GetFullPath(executable))) throw new IOException("実行ファイルが見つからないため登録できません");
                var desired = enabled ? DeviceLoginStartupState.Registered : DeviceLoginStartupState.Unregistered;
                if (current.State == desired) return current;
                registration.Update(expected.Revision, enabled ? Command : null);
                var actual = Project(registration.Read());
                if (actual.State != desired) throw new IOException("Windowsの登録結果を確認できません。再取得してください");
                return actual;
            }).ConfigureAwait(false);
        }
        finally { access.Release(); }
    }
    public Task OpenWindowsSettingsAsync() => Task.Run(registration.OpenWindowsSettings);
}

internal sealed class WindowsLoginStartupRegistration : IDeviceLoginStartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "FlowDeck";
    private static readonly object Gate = new();
    public bool IsDemo => false;
    private static DeviceLoginStartupValue Read(RegistryKey? key)
    {
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? DeviceLoginStartupValue.Missing : DeviceLoginStartupValue.From(key!.GetValueKind(ValueName), value);
    }
    public DeviceLoginStartupValue Read()
    { lock (Gate) { using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false); return Read(key); } }
    public void Update(string expectedRevision, string? command)
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            // Windows does not offer an atomic compare-and-swap for a Run value. Recheck immediately before writing;
            // the controller also reads back afterward. Never restore over an external writer after a mismatch.
            if (Read(key).Revision != expectedRevision) throw new IOException("Windowsの登録が変わりました。再取得してください");
            if (command is null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }
    public void OpenWindowsSettings() { using var process = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
}

/// <summary>Development-only registration. Never accesses the registry, startup folders or a shell.</summary>
public sealed class DemoLoginStartupRegistration : IDeviceLoginStartupRegistration
{
    public bool IsDemo => true;
    public DeviceLoginStartupValue Value { get; set; } = DeviceLoginStartupValue.Missing;
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public int SettingsOpened { get; private set; }
    public bool DenyRead { get; set; }
    public bool DenyWrite { get; set; }
    public Action? BeforeWrite { get; set; }
    public Action? AfterWrite { get; set; }
    public DeviceLoginStartupValue Read() { Reads++; if (DenyRead) throw new UnauthorizedAccessException(); return Value; }
    public void Update(string expectedRevision, string? command)
    {
        BeforeWrite?.Invoke();
        if (Value.Revision != expectedRevision) throw new IOException("Windowsの登録が変わりました。再取得してください");
        if (DenyWrite) throw new UnauthorizedAccessException();
        Writes++; Value = command is null ? DeviceLoginStartupValue.Missing : DeviceLoginStartupValue.From(RegistryValueKind.String, command); AfterWrite?.Invoke();
    }
    public void OpenWindowsSettings() => SettingsOpened++;
}
