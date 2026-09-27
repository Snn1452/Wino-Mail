using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Wino.NotificationHost.Contracts;
using Wino.Platform.Windows;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundNotificationHostClient
{
    public Task ShowAsync(
        NotificationHostApplication application,
        AppNotification notification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToastNotificationDispatcher.Show($"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.GetApplicationId(application)}", notification.Payload, notification.Tag, notification.Group);
        return Task.CompletedTask;
    }

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToastNotificationDispatcher.RemoveByTag($"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.GetApplicationId(application)}", tag);
        return Task.CompletedTask;
    }
}
