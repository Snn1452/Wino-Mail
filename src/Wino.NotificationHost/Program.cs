using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost.Contracts;

namespace Wino.NotificationHost;

public static class NotificationHostRuntime
{
    private const string AppNotificationActivatedCommandLinePrefix = "----AppNotificationActivated:";
    private static readonly TimeSpan StaleEnvelopeAge = TimeSpan.FromHours(24);

    public static int Run(string[] args)
    {
        NotificationHostLogger.Write("startup", exception: null);

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            NotificationHostLogger.Write("winrt-initialized");

            var package = Package.Current;
            ReleaseIdentity.Initialize(package.InstalledLocation.Path, package.Id.Name, package.Id.Publisher, package.Id.FamilyName);
            NotificationHostLogger.Write("identity-initialized");

            var localCachePath = ApplicationData.Current.LocalCacheFolder.Path;
            NotificationHostLogger.Write(
                "package",
                message: $"Package={package.Id.Name}; AUMID={CurrentAppIdentity.GetAppUserModelId()}; LocalCache={localCachePath}");
            _ = NotificationHostFileStore.CleanupStaleFiles(localCachePath, StaleEnvelopeAge);

            if (Environment.CommandLine.Contains(AppNotificationActivatedCommandLinePrefix, StringComparison.OrdinalIgnoreCase))
                return RunActivationBridge(localCachePath);

            if (!TryParseRequestId(args, out var requestId))
                throw new ArgumentException("Notification host requires a valid request ID.");

            NotificationHostLogger.Write("request-parsed", requestId);
            ProcessRequest(localCachePath, requestId);
            NotificationHostLogger.Write("request-completed", requestId);
            return 0;
        }
        catch (Exception ex)
        {
            NotificationHostLogger.Write("failed", exception: ex);
            return 1;
        }
    }

    private static void ProcessRequest(string localCachePath, Guid requestId)
    {
        try
        {
            NotificationHostLogger.Write("request-reading", requestId);
            var request = NotificationHostFileStore.ReadRequest(localCachePath, requestId);
            var currentAppUserModelId = CurrentAppIdentity.GetAppUserModelId();
            NotificationHostLogger.Write(
                "request-read",
                requestId,
                message: $"AUMID={currentAppUserModelId}; Application={request.Application}; Operation={request.Operation}");

            if (!NotificationHostApplicationIds.TryResolveFromAppUserModelId(currentAppUserModelId, out var currentApplication) ||
                currentApplication != request.Application)
            {
                throw new InvalidDataException("The notification request does not match the current application identity.");
            }

            var notificationManager = AppNotificationManager.Default;
            NotificationHostLogger.Write("register-start", requestId);
            notificationManager.Register();
            NotificationHostLogger.Write("register-complete", requestId);

            try
            {
                NotificationHostLogger.Write("execute-start", requestId);
                ExecuteRequest(notificationManager, request);
                NotificationHostLogger.Write(request.Operation.ToString(), requestId);
                NotificationHostLogger.Write("execute-complete", requestId);
            }
            finally
            {
                try
                {
                    notificationManager.Unregister();
                }
                catch (Exception ex)
                {
                    NotificationHostLogger.Write("unregister-failed", requestId, ex);
                }
            }
        }
        finally
        {
            NotificationHostFileStore.TryDeleteRequest(localCachePath, requestId);
        }
    }

    private static void ExecuteRequest(AppNotificationManager manager, NotificationHostRequest request)
    {
        switch (request.Operation)
        {
            case NotificationHostOperation.Show:
                NotificationPayloadValidator.Validate(request.Payload!);
                var notification = new AppNotification(request.Payload!);
                if (!string.IsNullOrWhiteSpace(request.Tag))
                    notification.Tag = request.Tag;
                if (!string.IsNullOrWhiteSpace(request.Group))
                    notification.Group = request.Group;
                manager.Show(notification);
                break;
            case NotificationHostOperation.RemoveByTag:
                manager.RemoveByTagAsync(request.Tag!).AsTask().GetAwaiter().GetResult();
                break;
            case NotificationHostOperation.RemoveByTagAndGroup:
                manager.RemoveByTagAndGroupAsync(request.Tag!, request.Group!).AsTask().GetAwaiter().GetResult();
                break;
            case NotificationHostOperation.RemoveGroup:
                manager.RemoveByGroupAsync(request.Group!).AsTask().GetAwaiter().GetResult();
                break;
            case NotificationHostOperation.RemoveAll:
                manager.RemoveAllAsync().AsTask().GetAwaiter().GetResult();
                break;
            default:
                throw new InvalidDataException("Unknown notification host operation.");
        }
    }

    private static int RunActivationBridge(string localCachePath)
    {
        using var invoked = new ManualResetEventSlim();
        Exception? failure = null;
        var handled = 0;
        void HandleActivation(string argument, IReadOnlyDictionary<string, string> userInput)
        {
            if (Interlocked.Exchange(ref handled, 1) != 0)
                return;

            try
            {
                ForwardActivation(localCachePath, argument, userInput);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                invoked.Set();
            }
        }

        var currentAppUserModelId = CurrentAppIdentity.GetAppUserModelId();
        if (!NotificationHostApplicationIds.TryResolveFromAppUserModelId(currentAppUserModelId, out var application))
            throw new InvalidOperationException("Current AUMID is not a Wino notification host identity.");

        using var comServer = new NotificationActivationComServer(GetActivatorClassId(application), HandleActivation);

        if (!invoked.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Timed out waiting for notification activation arguments.");

        if (failure != null)
            throw failure;

        return 0;
    }

    private static void ForwardActivation(
        string localCachePath,
        string argument,
        IReadOnlyDictionary<string, string> userInput)
    {
        var currentAppUserModelId = CurrentAppIdentity.GetAppUserModelId();
        if (!NotificationHostApplicationIds.TryResolveFromAppUserModelId(currentAppUserModelId, out var application))
            throw new InvalidOperationException("Current AUMID is not a Wino notification host identity.");

        var activationId = Guid.NewGuid();
        var envelope = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            application,
            argument,
            userInput);

        NotificationHostFileStore.WriteActivationAsync(localCachePath, activationId, envelope)
            .GetAwaiter()
            .GetResult();

        try
        {
            var mainAppUserModelId = $"{Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.Main}";
            _ = PackagedApplicationActivator.Activate(
                mainAppUserModelId,
                NotificationHostLaunchArguments.CreateForwardedActivation(activationId));
            NotificationHostLogger.Write("forward-activation", activationId);
        }
        catch
        {
            NotificationHostFileStore.TryDeleteActivation(localCachePath, activationId);
            throw;
        }
    }

    private static Guid GetActivatorClassId(NotificationHostApplication application) => application switch
    {
        NotificationHostApplication.Mail => ReleaseIdentity.Current.NotificationActivatorIds["Mail"],
        NotificationHostApplication.Calendar => ReleaseIdentity.Current.NotificationActivatorIds["Calendar"],
        NotificationHostApplication.People => ReleaseIdentity.Current.NotificationActivatorIds["People"],
        NotificationHostApplication.Tasks => ReleaseIdentity.Current.NotificationActivatorIds["Tasks"],
        _ => throw new ArgumentOutOfRangeException(nameof(application))
    };

    private static bool TryParseRequestId(string[] args, out Guid requestId)
    {
        requestId = Guid.Empty;
        return args.Length == 2 &&
               string.Equals(args[0], NotificationHostLaunchArguments.RequestSwitch, StringComparison.Ordinal) &&
               Guid.TryParseExact(args[1], "D", out requestId) &&
               requestId != Guid.Empty;
    }
}
