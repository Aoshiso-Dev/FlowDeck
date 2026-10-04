using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

public interface IDeviceOpenRgbRuntimeInspector
{
    Task<DeviceOpenRgbRuntimeInspection> InspectAsync(CancellationToken ct);
}

internal sealed class DeviceOpenRgbRuntimeInspector : IDeviceOpenRgbRuntimeInspector
{
    private readonly string directory;
    private readonly OpenRgbRuntimePackage package;
    private readonly bool requireProtected;
    internal DeviceOpenRgbRuntimeInspector(string directory, OpenRgbRuntimePackage package, bool requireProtected)
    { this.directory = directory; this.package = package; this.requireProtected = requireProtected; }
    internal static DeviceOpenRgbRuntimeInspector ForInstalledService() =>
        new(Path.Combine(ServiceEnvironment.InstallationDirectory, ServicePackage.OpenRgbDirectory), OpenRgbRuntimePackage.FlowDeckPinned, true);

    public Task<DeviceOpenRgbRuntimeInspection> InspectAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        DeviceOpenRgbRuntimeInspection Result(OpenRgbRuntimeInspectionStatus status, bool accepted = false) =>
            new(status, accepted, package.ExpectedVersion);
        var rootObserved = false;
        try
        {
            // GetAttributes preserves access-denied errors; Directory.Exists would report false.
            var attributes = File.GetAttributes(directory);
            rootObserved = true;
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                return Result(OpenRgbRuntimeInspectionStatus.Invalid);
            using var lease = package.Acquire(directory, requireProtected);
            ct.ThrowIfCancellationRequested();
            var accepted = true;
            try { OpenRgbOwnedProcess.RequireAcceptedDetection(OpenRgbStartupKind.AsrockPolychromeUsb); }
            catch (IOException) { accepted = false; }
            return Result(OpenRgbRuntimeInspectionStatus.Verified, accepted);
        }
        catch (FileNotFoundException) { return Result(rootObserved ? OpenRgbRuntimeInspectionStatus.Invalid : OpenRgbRuntimeInspectionStatus.NotInstalled); }
        catch (DirectoryNotFoundException) { return Result(rootObserved ? OpenRgbRuntimeInspectionStatus.Invalid : OpenRgbRuntimeInspectionStatus.NotInstalled); }
        catch (UnauthorizedAccessException) { return Result(OpenRgbRuntimeInspectionStatus.AccessDenied); }
        catch (IOException) { return Result(OpenRgbRuntimeInspectionStatus.Invalid); }
        catch (System.Security.SecurityException) { return Result(OpenRgbRuntimeInspectionStatus.AccessDenied); }
    }, ct);
}

// In-memory status source for the explicit demo host. It never inspects an installed runtime.
public sealed class DemoOpenRgbRuntimeInspector : IDeviceOpenRgbRuntimeInspector
{
    public DeviceOpenRgbRuntimeInspection Result { get; set; } = new(OpenRgbRuntimeInspectionStatus.Verified, true, "1.0-demo");
    public Func<CancellationToken, Task>? Before { get; set; }
    public int Calls { get; private set; }
    public async Task<DeviceOpenRgbRuntimeInspection> InspectAsync(CancellationToken ct)
    {
        Calls++; if (Before is not null) await Before(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); return Result with { };
    }
}
