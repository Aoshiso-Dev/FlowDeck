using System.Runtime.InteropServices;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public sealed class WindowsDeviceHandleWatchTests
{
    private sealed class Api : WindowsDeviceHandleWatch.IApi
    {
        public WindowsDeviceHandleWatch.Callback? Callback;
        public nint Context;
        public uint RegisterResult, UnregisterResult;
        public bool NotifyDuringRegister, NotifyDuringUnregister;
        public int Registers, Unregisters;
        public uint Notify(uint action = 5) => Callback!(72, Context, action, 0, 0);
        public uint Register(ref WindowsDeviceHandleWatch.Filter filter, nint context, WindowsDeviceHandleWatch.Callback callback, out nint notification)
        {
            Assert.Equal(416u, filter.Size); Assert.Equal(1u, filter.Type); Assert.Equal((nint)34, filter.DeviceHandle);
            Assert.Equal(0u, filter.Flags); Assert.Equal(0u, filter.Reserved);
            Registers++; Context = context; Callback = callback;
            if (NotifyDuringRegister) Assert.Equal(0u, Notify());
            notification = RegisterResult == 0 ? 72 : 0; return RegisterResult;
        }
        public uint Unregister(nint notification)
        {
            Assert.Equal((nint)72, notification); Unregisters++;
            if (NotifyDuringUnregister) Assert.Equal(0u, Task.Run(() => Notify()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            if (UnregisterResult == 0) { Callback = null; Context = 0; }
            return UnregisterResult;
        }
        public WindowsDeviceHandleWatch Open() => WindowsDeviceHandleWatch.Create(new() { Size = 416, Type = 1, DeviceHandle = 34 }, this);
    }
    [Theory] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(0)] [InlineData(999)]
    public void AnyEventPermanentlyRevokesTheHandleLifetimeAndNeverVetoesRemoval(uint action)
    {
        var api = new Api(); using var watch = api.Open(); watch.Check();
        Assert.Equal(0u, api.Notify(action)); Assert.Throws<IOException>(watch.Check);
        Assert.Equal(0u, api.Notify(3)); Assert.Throws<IOException>(watch.Check); // query-remove-failed is not a reset.
        watch.Dispose(); Assert.True(watch.ResourcesClosed); Assert.Equal(1, api.Unregisters);
    }
    [Fact] public void ANotificationBeforeRegistrationReturnsCannotPublishAnAlreadyStaleWatch()
    {
        var api = new Api { NotifyDuringRegister = true };
        Assert.Throws<IOException>(() => api.Open()); Assert.Equal(1, api.Unregisters); Assert.Null(api.Callback);
    }
    [Fact] public void FailedRegistrationDoesNotPretendThereIsAValidLifetime()
    {
        var api = new Api { RegisterResult = 3 };
        Assert.Throws<IOException>(() => api.Open()); Assert.Equal(0, api.Unregisters);
    }
    [Fact] public void UnregisterFailureRetainsTheCallbackContextForRetryButRevokesUse()
    {
        var api = new Api(); var watch = api.Open(); api.UnregisterResult = 3;
        Assert.Throws<IOException>(watch.Dispose); Assert.False(watch.ResourcesClosed); Assert.Throws<IOException>(watch.Check);
        GC.Collect(); GC.WaitForPendingFinalizers(); Assert.Equal(0u, api.Notify());
        api.UnregisterResult = 0; watch.Dispose(); Assert.True(watch.ResourcesClosed); Assert.Equal(2, api.Unregisters);
    }
    [Fact] public async Task FailedStartupCarriesAnUnfinishedNotificationRegistration()
    {
        var api = new Api { NotifyDuringRegister = true, UnregisterResult = 3 };
        var error = Assert.Throws<DeviceStartupException>(() => api.Open()); Assert.False(error.ResourcesClosed);
        Assert.Equal(0u, api.Notify()); api.UnregisterResult = 0; await error.DisposeAsync();
        Assert.True(error.ResourcesClosed); Assert.Equal(2, api.Unregisters);
    }
    [Fact] public async Task UnregistrationCanDrainConcurrentCallbacksWithoutTakingTheDisposalLock()
    {
        var api = new Api { NotifyDuringUnregister = true }; var watch = api.Open();
        await Task.WhenAll(Task.Run(watch.Dispose), Task.Run(watch.Dispose));
        Assert.True(watch.ResourcesClosed); Assert.Equal(1, api.Unregisters);
    }
    [Fact] public void NativeStructureAndRealOsRegistrationWorkWithoutOpeningAnyDevice()
    {
        Assert.Equal(416, Marshal.SizeOf<WindowsDeviceHandleWatch.Filter>());
        Assert.Equal((nint)16, Marshal.OffsetOf<WindowsDeviceHandleWatch.Filter>(nameof(WindowsDeviceHandleWatch.Filter.DeviceHandle)));
        using var watch = WindowsDeviceHandleWatch.CreateInterfaceWatch(Guid.NewGuid());
        watch.Check(); watch.Dispose(); Assert.True(watch.ResourcesClosed); Assert.Throws<IOException>(watch.Check);
    }
}
