using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundNotificationHostClient
{
    private const string DefaultGroup = "wino";

    public Task ShowAsync(
        NotificationHostApplication application,
        string payload,
        string? tag = null,
        string? group = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload, nameof(payload));
        cancellationToken.ThrowIfCancellationRequested();
        NotificationPayloadValidator.Validate(payload);

        var document = new XmlDocument();
        document.LoadXml(payload);
        var toast = new ToastNotification(document)
        {
            Group = string.IsNullOrWhiteSpace(group) ? DefaultGroup : group
        };

        if (!string.IsNullOrWhiteSpace(tag))
            toast.Tag = tag;

        ToastNotificationManager.CreateToastNotifier(GetAppUserModelId(application)).Show(toast);
        return Task.CompletedTask;
    }

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        cancellationToken.ThrowIfCancellationRequested();

        ToastNotificationManager.History.Remove(tag, DefaultGroup, GetAppUserModelId(application));
        return Task.CompletedTask;
    }

    public async Task ShowAndWaitForSmokeAsync(
        NotificationHostApplication application,
        string payload,
        string tag,
        CancellationToken cancellationToken = default)
    {
        await ShowAsync(application, payload, tag, cancellationToken: cancellationToken).ConfigureAwait(false);

        var applicationId = NotificationHostApplicationIds.GetApplicationId(application);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var notifications = ToastNotificationManager.History.GetHistory(applicationId)
                ?? Array.Empty<ToastNotification>();

            if (notifications.Any(notification => string.Equals(notification.Tag, tag, StringComparison.Ordinal)))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"ToastNotificationManager.Show completed, but the notification was not present in Notification Center. ApplicationId={applicationId}; Tag={tag}");
    }

    private static string GetAppUserModelId(NotificationHostApplication application)
        => $"{Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.GetApplicationId(application)}";
}
