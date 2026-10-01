using Windows.ApplicationModel;
using Windows.Storage;
using Wino.NotificationHost.Contracts;
using Windows.UI.Notifications;

namespace Wino.NotificationHost;

public static class NotificationHostRuntime
{
    private const string AppNotificationActivatedCommandLinePrefix = "----AppNotificationActivated:";
    private static readonly TimeSpan StaleEnvelopeAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumHostLifetime = TimeSpan.FromSeconds(30);

    public static int Run(string[] args)
    {
        // Never retain a notification-host process indefinitely if a Windows notification or COM call blocks.
        NotificationHostLifetime.Start(MaximumHostLifetime);
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

            NotificationHostLogger.Write("execute-start", requestId, message: $"AUMID={currentAppUserModelId}");
            ExecuteRequest(currentAppUserModelId, request);
            NotificationHostLogger.Write(request.Operation.ToString(), requestId);

            if (request.Operation == NotificationHostOperation.Show &&
                request.Tag?.StartsWith("wino-smoke-test-", StringComparison.Ordinal) == true)
            {
                VerifySmokeNotification(currentAppUserModelId, request.Tag, requestId);
            }

            NotificationHostLogger.Write("execute-complete", requestId);
        }
        finally
        {
            NotificationHostFileStore.TryDeleteRequest(localCachePath, requestId);
        }
    }

    private static void VerifySmokeNotification(
        string appUserModelId,
        string tag,
        Guid requestId)
    {
        var applicationId = appUserModelId[(appUserModelId.LastIndexOf('!') + 1)..];

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var notifications = ToastNotificationManager.History.GetHistory(applicationId)
                ?? Array.Empty<ToastNotification>();

            if (notifications.Any(notification =>
                    string.Equals(notification.Tag, tag, StringComparison.Ordinal)))
            {
                NotificationHostLogger.Write(
                    "smoke-notification-present",
                    requestId,
                    message: $"AUMID={appUserModelId}; Count={notifications.Count}; Tag={tag}");
                return;
            }

            NotificationHostLogger.Write(
                "smoke-notification-poll",
                requestId,
                message: $"Attempt={attempt + 1}; Count={notifications.Count}; Tag={tag}");

            Thread.Sleep(200);
        }

        throw new InvalidOperationException(
            $"ToastNotificationManager.Show completed, but the smoke notification was not present in Notification Center. AUMID={appUserModelId}; Tag={tag}");
    }

    private static void ExecuteRequest(string appUserModelId, NotificationHostRequest request)
    {
        switch (request.Operation)
        {
            case NotificationHostOperation.Show:
                NotificationPayloadValidator.Validate(request.Payload!);

                var document = new Windows.Data.Xml.Dom.XmlDocument();
                document.LoadXml(request.Payload!);

                var toast = new ToastNotification(document);
                if (!string.IsNullOrWhiteSpace(request.Tag))
                    toast.Tag = request.Tag;
                if (!string.IsNullOrWhiteSpace(request.Group))
                    toast.Group = request.Group;

                var notifier = ToastNotificationManager.CreateToastNotifier(appUserModelId);
                NotificationHostLogger.Write(
                    "toast-setting",
                    message: $"AUMID={appUserModelId}; Setting={notifier.Setting}");

                if (notifier.Setting != NotificationSetting.Enabled)
                    throw new InvalidOperationException(
                        $"Windows toast notifications are not enabled for '{appUserModelId}'. Setting={notifier.Setting}.");

                notifier.Show(toast);
                break;

            case NotificationHostOperation.RemoveByTag:
                ToastNotificationManager.History.Remove(request.Tag!, string.Empty, appUserModelId);
                break;

            case NotificationHostOperation.RemoveByTagAndGroup:
                ToastNotificationManager.History.Remove(request.Tag!, request.Group!, appUserModelId);
                break;

            case NotificationHostOperation.RemoveGroup:
                ToastNotificationManager.History.RemoveGroup(request.Group!, appUserModelId);
                break;

            case NotificationHostOperation.RemoveAll:
                ToastNotificationManager.History.Clear(appUserModelId);
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
