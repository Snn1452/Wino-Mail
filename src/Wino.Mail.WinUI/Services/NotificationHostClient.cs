using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Microsoft.Windows.AppNotifications;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.WinUI.Services;

internal sealed class NotificationHostClient : INotificationHostClient
{
    public Task ShowAsync(
        NotificationHostApplication application,
        AppNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        var targetApplicationId = NotificationHostApplicationIds.GetToastTargetApplicationId(application);
        var appUserModelId = $"{Package.Current.Id.FamilyName}!{targetApplicationId}";

        var document = new XmlDocument();
        document.LoadXml(notification.Payload);

        var toast = new ToastNotification(document);

        if (!string.IsNullOrWhiteSpace(notification.Tag))
            toast.Tag = notification.Tag;

        if (!string.IsNullOrWhiteSpace(notification.Group))
            toast.Group = notification.Group;

        var notifier = ToastNotificationManager.CreateToastNotifier(appUserModelId);
        if (notifier.Setting != NotificationSetting.Enabled)
        {
            throw new InvalidOperationException(
                $"Windows toast notifications are not enabled for '{appUserModelId}'. Current setting: {notifier.Setting}.");
        }

        notifier.Show(toast);
        return Task.CompletedTask;
    }

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        cancellationToken.ThrowIfCancellationRequested();

        var targetApplicationId = NotificationHostApplicationIds.GetToastTargetApplicationId(application);
        var appUserModelId = $"{Package.Current.Id.FamilyName}!{targetApplicationId}";

        ToastNotificationManager.History.Remove(tag, string.Empty, appUserModelId);
        return Task.CompletedTask;
    }
}
