using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost.Contracts;

namespace Wino.Platform.Windows;

public static class NotificationHostDispatcher
{
    private static readonly TimeSpan StaleEnvelopeAge = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim DispatchGate = new(1, 1);

    public static async Task ShowAsync(
        NotificationHostApplication application,
        string payload,
        string? tag = null,
        string? group = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        await DispatchAsync(
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.Show,
                application,
                payload,
                tag,
                group),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        await DispatchAsync(
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.RemoveByTag,
                application,
                null,
                tag,
                null),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task DispatchAsync(
        NotificationHostRequest request,
        CancellationToken cancellationToken)
    {
        await DispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var package = Package.Current;
            var localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
            _ = NotificationHostFileStore.CleanupStaleFiles(localCachePath, StaleEnvelopeAge);

            var requestId = Guid.NewGuid();
            await NotificationHostFileStore.WriteRequestAsync(
                localCachePath,
                requestId,
                request,
                cancellationToken).ConfigureAwait(false);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var applicationId = NotificationHostApplicationIds.GetApplicationId(request.Application);
                var appUserModelId = $"{package.Id.FamilyName}!{applicationId}";
                PackagedApplicationActivator.Activate(
                    appUserModelId,
                    NotificationHostLaunchArguments.CreateRequest(requestId));
            }
            catch
            {
                NotificationHostFileStore.TryDeleteRequest(localCachePath, requestId);
                throw;
            }
        }
        finally
        {
            DispatchGate.Release();
        }
    }
}

internal static class PackagedApplicationActivator
{
    private static readonly Guid ActivationManagerClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ActivationManagerInterfaceId = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");

    public static unsafe uint Activate(string appUserModelId, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);

        var classId = ActivationManagerClassId;
        var interfaceId = ActivationManagerInterfaceId;
        var result = CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            5,
            ref interfaceId,
            out var instance);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            var virtualTable = *(void***)instance;
            var activateApplication =
                (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, uint, uint*, int>)virtualTable[3];

            fixed (char* appUserModelIdPointer = appUserModelId)
            fixed (char* argumentsPointer = arguments)
            {
                uint processId = 0;
                result = activateApplication(
                    instance,
                    appUserModelIdPointer,
                    argumentsPointer,
                    0,
                    &processId);

                Marshal.ThrowExceptionForHR(result);
                return processId;
            }
        }
        finally
        {
            _ = Marshal.Release(instance);
        }
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);
}
