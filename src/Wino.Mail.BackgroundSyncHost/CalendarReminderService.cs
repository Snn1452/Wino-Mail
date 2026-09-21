using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
namespace Wino.Mail.BackgroundSyncHost;
internal sealed class CalendarReminderService(ICalendarService calendarService,IAccountService accountService,INotificationBuilder notificationBuilder)
{
    private static readonly TimeSpan Interval=TimeSpan.FromSeconds(30);
    private readonly HashSet<string> _keys=[];
    private DateTime _last=DateTime.Now.AddSeconds(-30);
    public async Task RunAsync(CancellationToken t){using var timer=new PeriodicTimer(Interval);while(await timer.WaitForNextTickAsync(t)){var now=DateTime.Now;var accounts=await accountService.GetAccountsAsync();if(accounts.Any(a=>a.IsCalendarAccessGranted)){var due=await calendarService.CheckAndNotifyAsync(_last,now,_keys,t);foreach(var item in due)await notificationBuilder.CreateCalendarReminderNotificationAsync(item.CalendarItem,item.ReminderDurationInSeconds);} _last=now;}}
}