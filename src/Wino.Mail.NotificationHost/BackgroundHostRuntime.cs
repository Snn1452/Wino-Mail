using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Services;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.NotificationHost;

internal static class BackgroundHostRuntime
{
    private const int InboxSyncsPerFullSync = 20;

    public static int Run()
    {
        RunAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static async Task RunAsync()
    {
        using var mutex = new Mutex(
            true,
            ReleaseIdentity.Current.BackgroundHostMutexName,
            out var createdNew);

        if (!createdNew)
            return;

        var services = BuildServices();

        await services.GetRequiredService<IDatabaseService>()
            .InitializeAsync()
            .ConfigureAwait(false);

        await services.GetRequiredService<ITranslationService>()
            .InitializeAsync()
            .ConfigureAwait(false);

        await services.GetRequiredService<SynchronizationManagerInitializer>()
            .InitializeAsync()
            .ConfigureAwait(false);

        var accountService = services.GetRequiredService<IAccountService>();
        var synchronizationManager = services.GetRequiredService<ISynchronizationManager>();
        var preferences = services.GetRequiredService<IPreferencesService>();
        var counters = new ConcurrentDictionary<Guid, int>();

        await SynchronizeAllAccountsAsync(
            synchronizationManager,
            accountService,
            counters).ConfigureAwait(false);

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(Math.Max(1, preferences.EmailSyncIntervalMinutes)));

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            await SynchronizeAllAccountsAsync(
                synchronizationManager,
                accountService,
                counters).ConfigureAwait(false);
        }
    }

    private static async Task SynchronizeAllAccountsAsync(
        ISynchronizationManager synchronizationManager,
        IAccountService accountService,
        ConcurrentDictionary<Guid, int> counters)
    {
        if (IsForegroundRunning())
            return;

        using var syncMutex = new Mutex(
            false,
            ReleaseIdentity.Current.MailSynchronizationMutexName);

        var acquired = false;
        try
        {
            try
            {
                acquired = syncMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired || IsForegroundRunning())
                return;

            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

            foreach (var staleId in counters.Keys.Except(accounts.Select(a => a.Id)).ToList())
                counters.TryRemove(staleId, out _);

            await Task.WhenAll(
                accounts
                    .Where(a => a.IsMailAccessGranted)
                    .Select(a => SynchronizeAccountAsync(
                        synchronizationManager,
                        a,
                        counters)))
                .ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
                syncMutex.ReleaseMutex();
        }
    }

    private static async Task SynchronizeAccountAsync(
        ISynchronizationManager synchronizationManager,
        MailAccount account,
        ConcurrentDictionary<Guid, int> counters)
    {
        if (IsForegroundRunning() ||
            synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        var result = await synchronizationManager.SynchronizeMailAsync(
            new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.InboxOnly
            }).ConfigureAwait(false);

        if (result.CompletedState is not
            (SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted))
            return;

        var count = counters.AddOrUpdate(
            account.Id,
            1,
            static (_, current) => current + 1);

        if (count < InboxSyncsPerFullSync || IsForegroundRunning())
            return;

        await synchronizationManager.SynchronizeMailAsync(
            new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }).ConfigureAwait(false);

        counters[account.Id] = 0;
    }

    private static bool IsForegroundRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(
                    ReleaseIdentity.Current.MailHostMutexName,
                    out var mutex))
                return false;

            mutex.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.RegisterCoreServices();
        services.RegisterSharedServices();

        services.AddSingleton<ApplicationConfiguration>();
        services.AddSingleton<IApplicationConfiguration>(provider =>
            provider.GetRequiredService<ApplicationConfiguration>());
        services.AddSingleton<IConfigurationService, BackgroundConfigurationService>();
        services.AddSingleton<INativeAppService, BackgroundNativeAppService>();
        services.AddSingleton<IUserPresenceStateProvider, BackgroundPresenceStateProvider>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<INotificationBuilder, BackgroundNotificationBuilder>();

        var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false
            });

        var configuration = provider.GetRequiredService<ApplicationConfiguration>();
        configuration.ApplicationDataFolderPath =
            Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        configuration.ApplicationTempFolderPath =
            Windows.Storage.ApplicationData.Current.TemporaryFolder.Path;
        configuration.PublisherSharedFolderPath = string.Empty;
        configuration.AllowLegacyDataMigration =
            ReleaseIdentity.Current.AllowsLegacyMigration;
        configuration.ApplicationDisplayName =
            ReleaseIdentity.Current.DisplayNames["Mail"];

        return provider;
    }
}
