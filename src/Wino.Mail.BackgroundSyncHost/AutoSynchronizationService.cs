using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Mail.BackgroundSyncHost;
internal sealed class AutoSynchronizationService(ISynchronizationManager synchronizationManager,IAccountService accountService,IPreferencesService preferencesService)
{
    private const int InboxSyncsPerFullSync=20;
    private readonly ConcurrentDictionary<Guid,int> _counters=new();
    public Task RunAsync(CancellationToken token)=>Task.WhenAll(MailLoop(token),CalendarLoop(token));
    private async Task MailLoop(CancellationToken t){while(true){t.ThrowIfCancellationRequested();await MailTick(t);await Task.Delay(TimeSpan.FromMinutes(Math.Max(1,preferencesService.EmailSyncIntervalMinutes)),t);}}
    private async Task CalendarLoop(CancellationToken t){while(true){t.ThrowIfCancellationRequested();await CalendarTick(t);await Task.Delay(TimeSpan.FromMinutes(Math.Max(1,preferencesService.CalendarSyncIntervalMinutes)),t);}}
    private async Task MailTick(CancellationToken t){var accounts=await accountService.GetAccountsAsync();var ids=accounts.Select(a=>a.Id).ToHashSet();foreach(var id in _counters.Keys.Where(id=>!ids.Contains(id)).ToList())_counters.TryRemove(id,out _);await Task.WhenAll(accounts.Select(a=>MailAccount(a,t)));}
    private async Task CalendarTick(CancellationToken t){var accounts=await accountService.GetAccountsAsync();await Task.WhenAll(accounts.Where(a=>a.IsCalendarAccessGranted).Select(a=>CalendarAccount(a,t)));}
    private async Task CalendarAccount(MailAccount a,CancellationToken t){if(synchronizationManager.IsAccountSynchronizing(a.Id))return;await synchronizationManager.SynchronizeCalendarAsync(new CalendarSynchronizationOptions{AccountId=a.Id,Type=CalendarSynchronizationType.CalendarMetadata},t);}
    private async Task MailAccount(MailAccount a,CancellationToken t)
    {
        if(synchronizationManager.IsAccountSynchronizing(a.Id))return;
        if(a.IsContactAccessGranted)await synchronizationManager.SynchronizeContactsAsync(new ContactSynchronizationOptions{AccountId=a.Id,Type=ContactSynchronizationType.Delta},t);
        if(a.IsTaskAccessGranted&&!a.IsTaskReauthorizationRequired)await synchronizationManager.SynchronizeTasksAsync(new TaskSynchronizationOptions{AccountId=a.Id,Type=TaskSynchronizationType.Delta},t);
        if(!a.IsMailAccessGranted)return;
        var r=await synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions{AccountId=a.Id,Type=MailSynchronizationType.InboxOnly},t);
        if(r.CompletedState is not(SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted))return;
        var account=await accountService.GetAccountAsync(a.Id);if(account?.AttentionReason==AccountAttentionReason.InvalidCredentials)await accountService.ClearAccountAttentionAsync(a.Id);
        var n=_counters.AddOrUpdate(a.Id,1,(_,v)=>v+1);if(n<InboxSyncsPerFullSync)return;
        await synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions{AccountId=a.Id,Type=MailSynchronizationType.FullFolders},t);_counters[a.Id]=0;
    }
}