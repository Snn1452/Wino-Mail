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
    private readonly SemaphoreSlim _automaticSynchronizationGate = new(1, 1);
    private readonly SemaphoreSlim _automaticSynchronizationSemaphore = new(1, 1);

    public async Task RunAsync(CancellationToken token)
    {
        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        var mailLoop = RunMailLoopAsync(lifetimeCts.Token);
        var calendarLoop = RunCalendarLoopAsync(lifetimeCts.Token);
        var monitor = MonitorBackgroundModeAsync(lifetimeCts.Token);

        try
        {
            await Task.WhenAny(mailLoop, calendarLoop, monitor).ConfigureAwait(false);

            // Any loop completing ends the host. The monitor completes only when background mode
            // is disabled; the sync loops should not silently leave the other loop running.
            lifetimeCts.Cancel();

            await Task.WhenAll(mailLoop, calendarLoop, monitor).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            // Background mode was disabled or the host was asked to stop.
        }
    }

    private async Task MonitorBackgroundModeAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (preferencesService.AppCloseBehavior == AppCloseBehavior.Terminate)
                return;

            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        }
    }

    private async Task RunMailLoopAsync(CancellationToken token)
    {
        await RunIntervalLoopAsync(
            () => TimeSpan.FromMinutes(Math.Max(1, preferencesService.EmailSyncIntervalMinutes)),
            MailTickAsync,
            "Automatic mail/contact/task synchronization",
            token).ConfigureAwait(false);
    }

    private async Task RunCalendarLoopAsync(CancellationToken token)
    {
        await RunIntervalLoopAsync(
            () => TimeSpan.FromMinutes(Math.Max(1, preferencesService.CalendarSyncIntervalMinutes)),
            CalendarTickAsync,
            "Automatic calendar synchronization",
            token).ConfigureAwait(false);
    }

    private static async Task RunIntervalLoopAsync(
        Func<TimeSpan> intervalProvider,
        Func<CancellationToken, Task> tick,
        string operationName,
        CancellationToken token)
    {
        var pollInterval = TimeSpan.FromSeconds(30);
        DateTimeOffset? nextRunAt = null;
        TimeSpan? activeInterval = null;
        var runImmediately = true;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;
            var configuredInterval = intervalProvider();

            if (activeInterval is null)
            {
                activeInterval = configuredInterval;
                nextRunAt = now;
            }
            else if (activeInterval != configuredInterval)
            {
                var lastRunAt = nextRunAt!.Value - activeInterval.Value;
                nextRunAt = lastRunAt + configuredInterval;
                activeInterval = configuredInterval;
            }

            if (runImmediately)
            {
                nextRunAt = now;
                runImmediately = false;
            }

            var delay = nextRunAt!.Value - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(
                        delay < pollInterval ? delay : pollInterval,
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
                Log.Error(ex, "{OperationName} tick failed.", operationName);
            }

            activeInterval = intervalProvider();
            nextRunAt = DateTimeOffset.UtcNow + activeInterval.Value;
        }
    }

    private async Task MailTickAsync(CancellationToken token)
    {
        if (!await _automaticSynchronizationSemaphore.WaitAsync(0, token).ConfigureAwait(false))
            return;

        try
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        var ids = accounts.Select(account => account.Id).ToHashSet();

        foreach (var id in _counters.Keys.Where(id => !ids.Contains(id)).ToList())
            _counters.TryRemove(id, out _);

            await Task.WhenAll(accounts.Select(account => MailAccountAsync(account, token))).ConfigureAwait(false);
        }
        finally
        {
            _automaticSynchronizationSemaphore.Release();
        }
    }

    private async Task CalendarTickAsync(CancellationToken token)
    {
        await _automaticSynchronizationSemaphore.WaitAsync(token).ConfigureAwait(false);

        try
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

            await Task.WhenAll(
                    accounts
                        .Where(account => account.IsCalendarAccessGranted)
                        .Select(account => CalendarAccountAsync(account, token)))
                .ConfigureAwait(false);
        }
        finally
        {
            _automaticSynchronizationSemaphore.Release();
        }
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
