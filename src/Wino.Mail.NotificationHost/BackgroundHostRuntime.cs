using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Services;

namespace Wino.Mail.NotificationHost;

internal static class BackgroundHostRuntime
{
    private const int InboxSyncsPerFullSync = 20;
    private const string MutexSuffix = ".BackgroundMailHost";
    private const string LogDirectoryName = "BackgroundHost";
    private const string LogFileName = "BackgroundHost.log";

    public static int Run()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteLog("fatal", ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        var package = Package.Current;
        var mutexName = $"Local\\WinoMail.{package.Id.FamilyName}{MutexSuffix}";

        using var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
            return 0;

        var services = new ServiceCollection();
        services.RegisterCoreServices();
        services.RegisterSharedServices();
        services.AddSingleton<IConfigurationService, BackgroundConfigurationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<INotificationBuilder, BackgroundNotificationBuilder>();

        using var provider = services.BuildServiceProvider();

        var preferences = provider.GetRequiredService<IPreferencesService>();

        if (preferences.AppCloseBehavior == AppCloseBehavior.Terminate)
        {
            WriteLog("disabled");
            return 0;
        }

        if (preferences.AppCloseBehavior == AppCloseBehavior.RunInBackgroundWithTrayIcon)
        {
            await LaunchMainApplicationAsync().ConfigureAwait(false);
            WriteLog("launched-ui-for-tray");
            return 0;
        }

        var database = provider.GetRequiredService<IDatabaseService>();
        await database.InitializeAsync().ConfigureAwait(false);

        var translation = provider.GetRequiredService<ITranslationService>();
        await translation.InitializeAsync().ConfigureAwait(false);

        await provider.GetRequiredService<SynchronizationManagerInitializer>()
            .InitializeAsync()
            .ConfigureAwait(false);

        var accountService = provider.GetRequiredService<IAccountService>();
        var synchronizationManager = provider.GetRequiredService<ISynchronizationManager>();
        var configuration = provider.GetRequiredService<IConfigurationService>();

        WriteLog("started");

        await SynchronizeAllAccountsAsync(
            accountService,
            synchronizationManager,
            configuration,
            CancellationToken.None).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(
            Math.Clamp(preferences.EmailSyncIntervalMinutes, 1, 1440)));

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (preferences.AppCloseBehavior != AppCloseBehavior.RunInBackgroundWithoutTrayIcon)
                return 0;

            await SynchronizeAllAccountsAsync(
                accountService,
                synchronizationManager,
                configuration,
                CancellationToken.None).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task SynchronizeAllAccountsAsync(
        IAccountService accountService,
        ISynchronizationManager synchronizationManager,
        IConfigurationService configuration,
        CancellationToken cancellationToken)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!account.IsMailAccessGranted || synchronizationManager.IsAccountSynchronizing(account.Id))
                continue;

            var result = await synchronizationManager.SynchronizeMailAsync(
                new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.InboxOnly
                },
                cancellationToken).ConfigureAwait(false);

            if (result.CompletedState is not (
                SynchronizationCompletedState.Success or
                SynchronizationCompletedState.PartiallyCompleted))
                continue;

            if (account.Id == Guid.Empty)
                continue;

            var fullSyncCounterKey = $"BackgroundHost.FullSyncCount.{account.Id:N}";
            var count = configuration.Get(fullSyncCounterKey, 0) + 1;
            configuration.Set(fullSyncCounterKey, count);

            if (count < InboxSyncsPerFullSync)
                continue;

            await synchronizationManager.SynchronizeMailAsync(
                new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                },
                cancellationToken).ConfigureAwait(false);

            configuration.Set(fullSyncCounterKey, 0);
        }
    }

    private static async Task LaunchMainApplicationAsync()
    {
        var expectedAumid = $"{Package.Current.Id.FamilyName}!App";
        var entries = await Package.Current.GetAppListEntriesAsync();

        var app = entries.FirstOrDefault(entry =>
            string.Equals(entry.AppUserModelId, expectedAumid, StringComparison.OrdinalIgnoreCase));

        if (app == null)
            throw new InvalidOperationException($"Could not find main Wino Mail application entry '{expectedAumid}'.");

        if (!await app.LaunchAsync())
            throw new InvalidOperationException("Windows refused to launch the Wino Mail UI application.");
    }

    private static void WriteLog(string operation, Exception? exception = null)
    {
        try
        {
            var directory = Path.Combine(
                ApplicationData.Current.LocalCacheFolder.Path,
                LogDirectoryName);
            Directory.CreateDirectory(directory);

            var line = $"{DateTimeOffset.UtcNow:O}\t{operation}\t{exception?.GetType().FullName ?? "-"}\t{exception?.Message ?? "-"}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, LogFileName), line);
        }
        catch
        {
        }
    }
}

