using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.NotificationHost.Contracts;

namespace Wino.Platform.Windows;

public static class NotificationHostDispatcher
{
    public static Task ShowAsync(
        NotificationHostApplication application,
        string payload,
        string? tag = null,
        string? group = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var package = Package.Current;
        var applicationId = NotificationHostApplicationIds.GetToastTargetApplicationId(application);
        var appUserModelId = $"{package.Id.FamilyName}!{applicationId}";

        var document = new XmlDocument();
        document.LoadXml(payload);

        var toast = new ToastNotification(document);
        if (!string.IsNullOrWhiteSpace(tag))
            toast.Tag = tag;
        if (!string.IsNullOrWhiteSpace(group))
            toast.Group = group;

        var notifier = ToastNotificationManager.CreateToastNotifier(appUserModelId);
        if (notifier.Setting != NotificationSetting.Enabled)
        {
            throw new InvalidOperationException(
                $"Windows toast notifications are not enabled for '{appUserModelId}'. Current setting: {notifier.Setting}.");
        }

        notifier.Show(toast);
        toast.Dismissed += (_, _) => { };
        toast.Failed += (_, args) =>
            System.Diagnostics.Debug.WriteLine(
                $"Wino Mail background toast failed. ErrorCode={args.ErrorCode}; AUMID={appUserModelId}; Tag={tag}");

        return Task.CompletedTask;
    }

    public static Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var package = Package.Current;
        var applicationId = NotificationHostApplicationIds.GetToastTargetApplicationId(application);
        var appUserModelId = $"{package.Id.FamilyName}!{applicationId}";

        ToastNotificationManager.History.Remove(tag, string.Empty, appUserModelId);
        return Task.CompletedTask;
    }

    public static async Task<bool> WaitForNotificationAsync(
        NotificationHostApplication application,
        string tag,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var applicationId = NotificationHostApplicationIds.GetToastTargetApplicationId(application);
        var deadline = DateTime.UtcNow + timeout;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ToastNotificationManager.History
                .GetHistory(applicationId)
                .Any(notification => string.Equals(notification.Tag, tag, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }
}
