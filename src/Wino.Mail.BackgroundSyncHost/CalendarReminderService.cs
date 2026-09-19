using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class CalendarReminderService(
    ICalendarService calendarService,
    IAccountService accountService,
    INotificationBuilder notificationBuilder)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly HashSet<string> _keys = [];
    private DateTime _last = DateTime.Now.AddSeconds(-30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await CheckRemindersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background calendar reminder check failed.");
            }
        }
    }

    private async Task CheckRemindersAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

        if (accounts.Any(account => account.IsCalendarAccessGranted))
        {
            var dueItems = await calendarService
                .CheckAndNotifyAsync(_last, now, _keys, cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in dueItems)
            {
                await notificationBuilder
                    .CreateCalendarReminderNotificationAsync(
                        item.CalendarItem,
                        item.ReminderDurationInSeconds)
                    .ConfigureAwait(false);
            }
        }

        _last = now;
    }
}
