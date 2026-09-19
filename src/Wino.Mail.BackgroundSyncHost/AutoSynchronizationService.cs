using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class AutoSynchronizationService(
    ISynchronizationManager synchronizationManager,
    IAccountService accountService,
    IPreferencesService preferencesService)
{
    private const int InboxSyncsPerFullSync = 20;
    private readonly ConcurrentDictionary<Guid, int> _counters = new();

    public Task RunAsync(CancellationToken token)
        => Task.WhenAll(
            RunMailLoopAsync(token),
            RunCalendarLoopAsync(token));

    private async Task RunMailLoopAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await MailTickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Automatic mail/contact/task synchronization tick failed.");
            }

            await Task.Delay(
                    TimeSpan.FromMinutes(Math.Max(1, preferencesService.EmailSyncIntervalMinutes)),
                    token)
                .ConfigureAwait(false);
        }
    }

    private async Task RunCalendarLoopAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await CalendarTickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Automatic calendar synchronization tick failed.");
            }

            await Task.Delay(
                    TimeSpan.FromMinutes(Math.Max(1, preferencesService.CalendarSyncIntervalMinutes)),
                    token)
                .ConfigureAwait(false);
        }
    }

    private async Task MailTickAsync(CancellationToken token)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var ids = accounts.Select(account => account.Id).ToHashSet();

        foreach (var id in _counters.Keys.Where(id => !ids.Contains(id)).ToList())
            _counters.TryRemove(id, out _);

        await Task.WhenAll(accounts.Select(account => MailAccountAsync(account, token))).ConfigureAwait(false);
    }

    private async Task CalendarTickAsync(CancellationToken token)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

        await Task.WhenAll(
                accounts
                    .Where(account => account.IsCalendarAccessGranted)
                    .Select(account => CalendarAccountAsync(account, token)))
            .ConfigureAwait(false);
    }

    private async Task CalendarAccountAsync(MailAccount account, CancellationToken token)
    {
        if (synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        await synchronizationManager.SynchronizeCalendarAsync(
                new CalendarSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = CalendarSynchronizationType.CalendarMetadata
                },
                token)
            .ConfigureAwait(false);
    }

    private async Task MailAccountAsync(MailAccount account, CancellationToken token)
    {
        if (synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        if (account.IsContactAccessGranted)
        {
            await synchronizationManager.SynchronizeContactsAsync(
                    new ContactSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = ContactSynchronizationType.Delta
                    },
                    token)
                .ConfigureAwait(false);
        }

        if (account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired)
        {
            await synchronizationManager.SynchronizeTasksAsync(
                    new TaskSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = TaskSynchronizationType.Delta
                    },
                    token)
                .ConfigureAwait(false);
        }

        if (!account.IsMailAccessGranted)
            return;

        var result = await synchronizationManager.SynchronizeMailAsync(
                new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.InboxOnly
                },
                token)
            .ConfigureAwait(false);

        if (result.CompletedState is not (
                SynchronizationCompletedState.Success or
                SynchronizationCompletedState.PartiallyCompleted))
            return;

        var persistedAccount = await accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        if (persistedAccount?.AttentionReason == AccountAttentionReason.InvalidCredentials)
            await accountService.ClearAccountAttentionAsync(account.Id).ConfigureAwait(false);

        var count = _counters.AddOrUpdate(account.Id, 1, (_, value) => value + 1);
        if (count < InboxSyncsPerFullSync)
            return;

        await synchronizationManager.SynchronizeMailAsync(
                new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                },
                token)
            .ConfigureAwait(false);

        _counters[account.Id] = 0;
    }
}
