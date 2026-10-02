using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundNotificationHostClient
{
    private static readonly Guid ActivationManagerClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ActivationManagerInterfaceId = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");
    private static readonly TimeSpan StaleEnvelopeAge = TimeSpan.FromHours(24);

    private readonly string _localCachePath = ApplicationData.Current.LocalCacheFolder.Path;

    public BackgroundNotificationHostClient()
    {
        _ = NotificationHostFileStore.CleanupStaleFiles(_localCachePath, StaleEnvelopeAge);
    }

    public Task ShowAsync(
        NotificationHostApplication application,
        string payload,
        string? tag = null,
        string? group = null,
        CancellationToken cancellationToken = default)
        => DispatchAsync(
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.Show,
                application,
                payload,
                tag,
                group),
            cancellationToken);

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
        => DispatchAsync(
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.RemoveByTag,
                application,
                null,
                tag,
                null),
            cancellationToken);

    public async Task ShowAndWaitForSmokeAsync(
        NotificationHostApplication application,
        string payload,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var request = new NotificationHostRequest(
            DateTimeOffset.UtcNow,
            NotificationHostOperation.Show,
            application,
            payload,
            tag,
            null);

        var processId = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (processId == 0)
            throw new InvalidOperationException("The packaged notification host activation returned process id 0.");

        using var process = Process.GetProcessById(checked((int)processId));

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The packaged notification host exited with code {process.ExitCode} while handling smoke tag '{tag}'.");
    }

    private async Task<uint> DispatchAsync(
        NotificationHostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Payload, nameof(request.Payload));
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Guid.NewGuid();

        await NotificationHostFileStore
            .WriteRequestAsync(_localCachePath, requestId, request, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var applicationId = NotificationHostApplicationIds.GetApplicationId(request.Application);
            var appUserModelId = $"{Package.Current.Id.FamilyName}!{applicationId}";
            return ActivateApplication(appUserModelId, NotificationHostLaunchArguments.CreateRequest(requestId));
        }
        catch
        {
            NotificationHostFileStore.TryDeleteRequest(_localCachePath, requestId);
            throw;
        }
    }

    private static unsafe uint ActivateApplication(string appUserModelId, string arguments)
    {
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
