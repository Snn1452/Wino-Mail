using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Wino.NotificationHost.Contracts;
using Wino.Platform.Windows;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundNotificationHostClient
{
    public Task ShowAsync(NotificationHostApplication application, AppNotification notification, CancellationToken cancellationToken = default)
        => NotificationHostDispatcher.ShowAsync(
            application,
            notification.Payload,
            notification.Tag,
            notification.Group,
            cancellationToken);

    public Task RemoveByTagAsync(NotificationHostApplication application, string tag, CancellationToken cancellationToken = default)
        => NotificationHostDispatcher.RemoveByTagAsync(application, tag, cancellationToken);
}
