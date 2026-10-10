using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.NotificationHost.Contracts;
using Wino.Platform.Windows;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundNotificationHostClient
{
public Task ShowAsync(
NotificationHostApplication application,
string payload,
string? tag = null,
string? group = null,
CancellationToken cancellationToken = default)
=> NotificationHostDispatcher.ShowAsync(application, payload, tag, group, cancellationToken);

public Task RemoveByTagAsync(
NotificationHostApplication application,
string tag,
CancellationToken cancellationToken = default)
=> NotificationHostDispatcher.RemoveByTagAsync(application, tag, cancellationToken);

public async Task ShowAndWaitForSmokeAsync(
NotificationHostApplication application,
string payload,
string tag,
CancellationToken cancellationToken = default)
{
await NotificationHostDispatcher.ShowAsync(application, payload, tag, cancellationToken: cancellationToken)
.ConfigureAwait(false);

var shown = await NotificationHostDispatcher.WaitForNotificationAsync(
application,
tag,
TimeSpan.FromSeconds(15),
cancellationToken)
.ConfigureAwait(false);

if (!shown)
throw new TimeoutException($"Windows notification history did not record smoke tag {tag}.");
}
}
