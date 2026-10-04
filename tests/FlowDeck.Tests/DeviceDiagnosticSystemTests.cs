using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Xunit.Abstractions;

namespace FlowDeck.Tests;

public sealed class DeviceDiagnosticSystemTests(ITestOutputHelper output)
{
    private sealed class Metadata : IWindowsDiagnosticMetadata
    {
        public string WindowsDirectory => @"C:\Windows";
        public DiagnosticDriverRegistration? Driver = new(1, @"\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_test\PawnIO.sys");
        public DiagnosticPackageRegistration? Package = new("2.2.0.0");
        public string? Version = "2.1.0.0";
        public Exception? DriverError, PackageError, FileError;
        public int FileReads;
        public DiagnosticDriverRegistration? ReadDriver() => DriverError is { } e ? throw e : Driver;
        public DiagnosticPackageRegistration? ReadPackage() => PackageError is { } e ? throw e : Package;
        public string? ReadFileVersion(string path) { FileReads++; if (FileError is { } e) throw e; return Version; }
    }
    [Fact] public void PackageRegistrationCannotMasqueradeAsTheInstalledOrLoadedDriverVersion()
    {
        var metadata = new Metadata(); var result = new PawnIoDiagnosticReader(metadata).Read();
        Assert.Equal("2.1.0.0", result.FileVersion); Assert.Equal("2.2.0.0", result.PackageVersion);
        Assert.Equal(DeviceDiagnosticReadState.Available, result.RegistrationState); Assert.False(result.LoadedVersionObserved);
        var environment = DeviceDiagnostics.EnvironmentInfo([result]);
        Assert.Equal("2.1.0.0", environment.InstalledKernelDriverVersion); Assert.NotNull(environment.OsArchitecture);
        Assert.All(environment.Components, c => { Assert.NotNull(DeviceDiagnostics.NumericVersion(c.Version)); Assert.True(Guid.TryParseExact(c.BuildId, "N", out _)); });
        Assert.DoesNotContain("FileRepository", JsonSerializer.Serialize(environment, Contract.Json));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void MissingOrInvalidKernelRegistrationDoesNotProbeAFileEvenIfThePackageIsInstalled(bool invalidType)
    {
        var metadata = new Metadata { Driver = invalidType ? new(16, @"C:\private\PawnIO.sys") : null };
        var result = new PawnIoDiagnosticReader(metadata).Read();
        Assert.Equal(invalidType ? DeviceDiagnosticReadState.Invalid : DeviceDiagnosticReadState.NotInstalled, result.RegistrationState);
        Assert.Equal(DeviceDiagnosticReadState.NotObserved, result.FileState); Assert.Null(result.FileVersion);
        Assert.Equal("2.2.0.0", result.PackageVersion); Assert.Equal(0, metadata.FileReads);
    }
    [Theory] [InlineData("denied", DeviceDiagnosticReadState.AccessDenied)] [InlineData("missing", DeviceDiagnosticReadState.Missing)]
    [InlineData("invalid", DeviceDiagnosticReadState.Invalid)] [InlineData("failed", DeviceDiagnosticReadState.Failed)]
    public void FailedFileObservationKeepsOtherEvidenceWithoutLeakingExceptions(string fault, DeviceDiagnosticReadState expected)
    {
        var secret = @"C:\Users\private-account\secret";
        var metadata = new Metadata { FileError = fault switch {
            "denied" => new UnauthorizedAccessException(secret), "missing" => new FileNotFoundException(secret),
            "invalid" => new InvalidDataException(secret), _ => new IOException(secret) } };
        var result = new PawnIoDiagnosticReader(metadata).Read(); Assert.Equal(expected, result.FileState);
        Assert.Null(result.FileVersion); Assert.Equal("2.2.0.0", result.PackageVersion);
        Assert.DoesNotContain("private-account", JsonSerializer.Serialize(result, Contract.Json));
    }
    [Fact] public void RegistryFailuresAreIndependentAndDoNotExposeRawData()
    {
        var metadata = new Metadata { DriverError = new UnauthorizedAccessException("PRIVATE"), PackageError = new IOException("PRIVATE") };
        var result = new PawnIoDiagnosticReader(metadata).Read();
        Assert.Equal(DeviceDiagnosticReadState.AccessDenied, result.RegistrationState);
        Assert.Equal(DeviceDiagnosticReadState.Failed, result.PackageState); Assert.Equal(0, metadata.FileReads);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, Contract.Json));
    }
    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\PawnIO.sys")]
    [InlineData(@"%SystemRoot%\System32\drivers\PawnIO.sys")]
    [InlineData(@"%windir%\System32\drivers\PawnIO.sys")]
    [InlineData(@"System32\drivers\PawnIO.sys")]
    [InlineData(@"\??\C:\Windows\System32\drivers\PawnIO.sys")]
    [InlineData("\"C:\\Windows\\System32\\drivers\\PawnIO.sys\"")]
    public void KnownLocalKernelImagePathFormsResolveWithoutEnvironmentExpansion(string path)
    {
        Assert.Equal(@"C:\Windows\System32\drivers\PawnIO.sys", PawnIoDiagnosticReader.ResolveImagePath(path, @"C:\Windows"));
    }
    [Theory]
    [InlineData(@"\\private-host\share\PawnIO.sys")]
    [InlineData(@"\??\UNC\private-host\share\PawnIO.sys")]
    [InlineData(@"\\?\GLOBALROOT\Device\PawnIO")]
    [InlineData(@"%USERPROFILE%\PawnIO.sys")]
    [InlineData(@"C:\private\PawnIO.sys:secret")]
    [InlineData(@"C:\private\PawnIO.sys --secret")]
    [InlineData(@"C:\private\other.sys")]
    [InlineData("C:\\private\\PawnIO.sys\n")]
    public void UntrustedImagePathFormsNeverReachTheFileReader(string path)
    {
        var metadata = new Metadata { Driver = new(1, path) }; var result = new PawnIoDiagnosticReader(metadata).Read();
        Assert.Equal(DeviceDiagnosticReadState.Invalid, result.FileState); Assert.Equal(0, metadata.FileReads);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result, Contract.Json));
    }
    [Theory] [InlineData("2.2.0.0 PRIVATE")] [InlineData("C:\\Users\\private")]
    [InlineData("2.2+private")] [InlineData("-1.2")] [InlineData("2.2\n")]
    public void NonNumericVersionMetadataIsNeverExported(string raw)
    {
        var metadata = new Metadata { Version = raw, Package = new(raw) }; var result = new PawnIoDiagnosticReader(metadata).Read();
        Assert.Equal(DeviceDiagnosticReadState.Invalid, result.FileState); Assert.Equal(DeviceDiagnosticReadState.Invalid, result.PackageState);
        Assert.Null(result.FileVersion); Assert.Null(result.PackageVersion);
        var untrusted = result with { Component = "PawnIO", FileState = DeviceDiagnosticReadState.Available, FileVersion = raw,
            PackageState = DeviceDiagnosticReadState.Available, PackageVersion = raw, LoadedVersionObserved = true };
        var sanitized = Assert.Single(DeviceDiagnostics.EnvironmentInfo([untrusted]).KernelDrivers!);
        Assert.Equal(DeviceDiagnosticReadState.Invalid, sanitized.FileState); Assert.Null(sanitized.FileVersion); Assert.Null(sanitized.PackageVersion);
        Assert.False(sanitized.LoadedVersionObserved);
    }
    [Fact] public void UnknownComponentsAndInvalidStatesCannotAddPrivateStringsToTheReport()
    {
        var known = new DeviceDiagnosticKernelDriver("PawnIO", (DeviceDiagnosticReadState)99, (DeviceDiagnosticReadState)99,
            "private", (DeviceDiagnosticReadState)99, "private", null);
        var environment = DeviceDiagnostics.EnvironmentInfo([known with { Component = "private" }, known]);
        Assert.Single(environment.KernelDrivers!); Assert.DoesNotContain("private", JsonSerializer.Serialize(environment, Contract.Json));
        Assert.Equal(DeviceDiagnosticReadState.Invalid, environment.KernelDrivers![0].RegistrationState);
    }
    [Fact] public void InstalledFileReaderUsesNumericPeMetadataWithoutLoadingOrExecutingTheFile()
    {
        using var directory = new ServiceTestDirectory(); var file = Path.Combine(directory.Path, "PawnIO.sys");
        File.Copy(typeof(WindowsDeviceDiagnosticSource).Assembly.Location, file);
        var value = new WindowsDiagnosticMetadata().ReadFileVersion(file);
        Assert.Equal(typeof(WindowsDeviceDiagnosticSource).Assembly.GetName().Version!.ToString(), value);
        File.WriteAllText(file, "not an executable resource"); Assert.Null(new WindowsDiagnosticMetadata().ReadFileVersion(file));
    }
    [Fact] public async Task TimedOutMetadataReadsStaySingleAndDoNotAccumulateBackgroundJobs()
    {
        using var finish = new ManualResetEventSlim(); var entered = new TaskCompletionSource(); var finished = new TaskCompletionSource(); var calls = 0;
        var source = new WindowsDeviceDiagnosticSource(() => { Interlocked.Increment(ref calls); entered.TrySetResult(); finish.Wait(); finished.TrySetResult(); return []; }, TimeSpan.FromMilliseconds(100));
        try
        {
            var first = source.ReadKernelDriversAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(DeviceDiagnosticReadState.TimedOut, Assert.Single(await first).RegistrationState);
            Assert.Equal(DeviceDiagnosticReadState.TimedOut, Assert.Single(await source.ReadKernelDriversAsync(default)).RegistrationState);
            Assert.Equal(1, calls);
        }
        finally { finish.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    [Fact] public async Task SlowMetadataDoesNotHoldTheControlGateAndDisconnectDoesNotWaitForTheFileRead()
    {
        using var finish = new ManualResetEventSlim(); var entered = new TaskCompletionSource(); var finished = new TaskCompletionSource();
        var source = new WindowsDeviceDiagnosticSource(() => { entered.TrySetResult(); finish.Wait(); finished.TrySetResult(); return []; });
        var driver = new GenericTestDriver("fan");
        await using var runtime = new DeviceControlRuntime([driver], ["user"], new TestLog(), diagnosticSystem: source);
        var hello = runtime.OpenSession("user");
        DeviceServiceRequest Query(long sequence, DeviceOperation operation) => new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), sequence, "PC", operation);
        var pending = runtime.ExecuteAsync(Query(1, DeviceOperation.ReadDiagnostics) with { Diagnostics = new() });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True((await runtime.ExecuteAsync(Query(2, DeviceOperation.Renew)).WaitAsync(TimeSpan.FromSeconds(1))).Success);
            var state = runtime.State(); var endpoint = Assert.Single(state.Endpoints);
            Assert.True((await runtime.ExecuteAsync(Query(3, DeviceOperation.SetDuty) with { TopologyRevision = state.Inventory.TopologyRevision,
                Targets = [new(endpoint.Target, endpoint.Generation, endpoint.PolicyRevision)], Duty = 60 }).WaitAsync(TimeSpan.FromSeconds(1))).Success);
            await runtime.DisconnectAsync(hello.Session).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False((await pending).Success); Assert.Equal(1, driver.Writes);
        }
        finally { finish.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    private sealed class Source : IDeviceDiagnosticSystemSource
    {
        public int Calls;
        public bool Fail;
        public Task<DeviceDiagnosticKernelDriver[]> ReadKernelDriversAsync(CancellationToken ct)
        {
            Calls++; ct.ThrowIfCancellationRequested(); if (Fail) throw new IOException("PRIVATE registry error");
            return Task.FromResult(new[] { new PawnIoDiagnosticReader(new Metadata()).Read() });
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StartupCapturesSystemMetadataOnlyOnDiagnosticRequestAndKeepsTheRestOnFailure(bool fail)
    {
        const string installation = "0123456789abcdef0123456789abcdef"; using var directory = new ServiceTestDirectory();
        var store = DeviceConfigurationCodec.Store(directory.Path);
        store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(installation))));
        var source = new Source { Fail = fail };
        await using var startup = await DeviceRuntimeStartup.CreateAsync(store, installation, () => new([], []), ["user"], new TestLog(), demo: true, diagnosticSystem: source);
        Assert.Equal(0, source.Calls); var hello = startup.Runtime.OpenSession("user");
        var reply = await startup.Runtime.ExecuteAsync(new(2, hello.Epoch, hello.Session, Guid.NewGuid().ToString("N"), 1, "PC", DeviceOperation.ReadDiagnostics, Diagnostics: new()));
        Assert.True(reply.Success); Assert.Equal(1, source.Calls);
        var report = JsonSerializer.Deserialize<DeviceDiagnosticReport>(reply.Diagnostics!.Data, Contract.Json)!;
        Assert.Equal(fail ? null : "2.1.0.0", report.Environment.InstalledKernelDriverVersion);
        Assert.Equal(fail, report.Omitted.Contains("installed-kernel-driver-version-not-observed"));
        Assert.Contains("loaded-kernel-driver-version-not-observed", report.Omitted);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(report, Contract.Json));
    }
    [Fact] public async Task ProductionMetadataReaderOnlyObservesRegistryAndFileVersions()
    {
        var values = await WindowsDeviceDiagnosticSource.Shared.ReadKernelDriversAsync(default);
        var driver = Assert.Single(values); Assert.Equal("PawnIO", driver.Component); Assert.False(driver.LoadedVersionObserved);
        Assert.True(Enum.IsDefined(driver.RegistrationState));
        if (driver.FileState == DeviceDiagnosticReadState.Available) Assert.NotNull(DeviceDiagnostics.NumericVersion(driver.FileVersion));
        output.WriteLine(JsonSerializer.Serialize(driver, Contract.Json));
    }
}
