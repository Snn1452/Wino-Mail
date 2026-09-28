using System;
using System.Threading;
using System.Threading.Tasks;
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
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(notification);
        AppNotificationManager.Default.Show(notification);
        return Task.CompletedTask;
    }

    public Task RemoveByTagAsync(
        NotificationHostApplication application,
        string tag,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return AppNotificationManager.Default.RemoveByTagAsync(tag).AsTask();
    }
}
