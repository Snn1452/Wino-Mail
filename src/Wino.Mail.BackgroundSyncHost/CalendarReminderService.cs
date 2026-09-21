using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class CalendarReminderService(
    ICalendarService calendarService,
    IAccountService accountService,
    IPreferencesService preferencesService,
    INotificationBuilder notificationBuilder)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly HashSet<string> _keys = [];
    private DateTime _last = DateTime.Now.AddSeconds(-30);

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (preferencesService.AppCloseBehavior == AppCloseBehavior.Terminate)
                return;

            var now = DateTime.Now;
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

            if (accounts.Any(account => account.IsCalendarAccessGranted))
            {
                var due = await calendarService
                    .CheckAndNotifyAsync(_last, now, _keys, token)
                    .ConfigureAwait(false);

                foreach (var item in due)
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
}
