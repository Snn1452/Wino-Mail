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
    private readonly ConcurrentDictionary<Guid, int> _inboxSyncCounters = [];

    public Task RunAsync(CancellationToken token)
        => Task.WhenAll(
            RunMailLoopAsync(token),
            RunCalendarLoopAsync(token));

    private Task RunMailLoopAsync(CancellationToken token)
        => RunIntervalLoopAsync(
            () => TimeSpan.FromMinutes(Math.Max(1, preferencesService.EmailSyncIntervalMinutes)),
            RunMailTickAsync,
            token);

    private Task RunCalendarLoopAsync(CancellationToken token)
        => RunIntervalLoopAsync(
            () => TimeSpan.FromMinutes(Math.Max(1, preferencesService.CalendarSyncIntervalMinutes)),
            RunCalendarTickAsync,
            token);

    private async Task RunIntervalLoopAsync(
        Func<TimeSpan> intervalProvider,
        Func<CancellationToken, Task> tick,
        CancellationToken token)
    {
        DateTimeOffset? nextRunAt = null;
        TimeSpan? activeInterval = null;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (preferencesService.AppCloseBehavior == AppCloseBehavior.Terminate)
                return;

            var now = DateTimeOffset.UtcNow;
            var configuredInterval = intervalProvider();

            if (activeInterval != configuredInterval)
            {
                var lastRunAt = nextRunAt.HasValue && activeInterval.HasValue
                    ? nextRunAt.Value - activeInterval.Value
                    : now;

                nextRunAt = lastRunAt + configuredInterval;
                activeInterval = configuredInterval;
            }

            var delay = nextRunAt!.Value - now;
            if (delay > TimeSpan.Zero)
            {
                var maxPoll = TimeSpan.FromSeconds(30);
                await Task.Delay(
                        delay <= maxPoll ? delay : maxPoll,
                        token)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                await tick(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Automatic background synchronization tick failed.");
            }

            activeInterval = intervalProvider();
            nextRunAt = DateTimeOffset.UtcNow + activeInterval.Value;
        }
    }

    private async Task RunMailTickAsync(CancellationToken token)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var currentIds = accounts.Select(a => a.Id).ToHashSet();

        foreach (var staleId in _inboxSyncCounters.Keys.Where(id => !currentIds.Contains(id)).ToList())
            _inboxSyncCounters.TryRemove(staleId, out _);

        foreach (var account in accounts)
        {
            token.ThrowIfCancellationRequested();

            if (synchronizationManager.IsAccountSynchronizing(account.Id))
                continue;

            try
            {
                if (account.IsContactAccessGranted)
                {
                    await synchronizationManager.SynchronizeContactsAsync(
                        new ContactSynchronizationOptions
                        {
                            AccountId = account.Id,
                            Type = ContactSynchronizationType.Delta
                        },
                        token).ConfigureAwait(false);
                }

                if (account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired)
                {
                    await synchronizationManager.SynchronizeTasksAsync(
                        new TaskSynchronizationOptions
                        {
                            AccountId = account.Id,
                            Type = TaskSynchronizationType.Delta
                        },
                        token).ConfigureAwait(false);
                }

                if (!account.IsMailAccessGranted)
                    continue;

                var result = await synchronizationManager.SynchronizeMailAsync(
                    new MailSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = MailSynchronizationType.InboxOnly
                    },
                    token).ConfigureAwait(false);

                if (result.CompletedState is not (
                    SynchronizationCompletedState.Success or
                    SynchronizationCompletedState.PartiallyCompleted))
                    continue;

                var persistedAccount = await accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
                if (persistedAccount?.AttentionReason == AccountAttentionReason.InvalidCredentials)
                    await accountService.ClearAccountAttentionAsync(account.Id).ConfigureAwait(false);

                var count = _inboxSyncCounters.AddOrUpdate(account.Id, 1, (_, current) => current + 1);
                if (count < InboxSyncsPerFullSync)
                    continue;

                await synchronizationManager.SynchronizeMailAsync(
                    new MailSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = MailSynchronizationType.FullFolders
                    },
                    token).ConfigureAwait(false);

                _inboxSyncCounters[account.Id] = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background synchronization failed for account {AccountId}.", account.Id);
            }
        }
    }

    private async Task RunCalendarTickAsync(CancellationToken token)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts.Where(a => a.IsCalendarAccessGranted))
        {
            token.ThrowIfCancellationRequested();

            if (synchronizationManager.IsAccountSynchronizing(account.Id))
                continue;

            try
            {
                await synchronizationManager.SynchronizeCalendarAsync(
                    new CalendarSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = CalendarSynchronizationType.CalendarMetadata
                    },
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Background calendar synchronization failed for account {AccountId}.", account.Id);
            }
        }
    }

    private static Task DelayUntilNextTickAsync(CancellationToken token)
        => Task.Delay(PreferencePollInterval, token);
}
