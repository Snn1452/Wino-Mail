using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.NotificationHost.Contracts;
using Wino.Platform.Windows.Services;
using Wino.Services;

namespace Wino.Mail.BackgroundSyncHost;

internal static class Program
{
    private static readonly string LockPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wino Mail",
        "background-sync-host.lock");

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        try
        {
            ReleaseIdentity.Initialize(
                Package.Current.InstalledLocation.Path,
                Package.Current.Id.Name,
                Package.Current.Id.Publisher,
                Package.Current.Id.FamilyName);

            using var instanceLock = AcquireInstanceLock();
            if (instanceLock is null)
                return 0;

            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Background synchronization host failed.");
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.RegisterSharedServices();
        services.RegisterCoreServices();

        services.AddSingleton<IConfigurationService>(_ => new ConfigurationService(useCache: false));
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IUserPresenceStateProvider, ShellUserPresenceStateProvider>();
        services.AddSingleton<IAppMetadataService, BackgroundAppMetadataService>();
        services.AddSingleton<IStatePersistanceService, BackgroundStatePersistenceService>();
        services.AddSingleton<BackgroundNotificationHostClient>();
        services.AddSingleton<INotificationBuilder, HeadlessNotificationBuilder>();
        services.AddSingleton<AutoSynchronizationService>();
        services.AddSingleton<CalendarReminderService>();

        await using var provider = services.BuildServiceProvider();

        ConfigureApplicationPaths(provider);
        ConfigureLogging(provider);

        var migrationPlan = await provider
            .GetRequiredService<IMigrationCoordinator>()
            .InspectAsync()
            .ConfigureAwait(false);

        if (migrationPlan.Status != Wino.Core.Domain.Models.Migration.MigrationStatus.NotRequired)
        {
            Serilog.Log.Information(
                "Background synchronization host is idle because database migration state is {MigrationStatus}.",
                migrationPlan.Status);
            return 0;
        }

        await provider
            .GetRequiredService<IDatabaseService>()
            .InitializeAsync()
            .ConfigureAwait(false);

        await provider
            .GetRequiredService<ITranslationService>()
            .InitializeAsync()
            .ConfigureAwait(false);

        await provider
            .GetRequiredService<SynchronizationManagerInitializer>()
            .InitializeAsync()
            .ConfigureAwait(false);

        await Task.WhenAll(
                provider.GetRequiredService<AutoSynchronizationService>().RunAsync(CancellationToken.None),
                provider.GetRequiredService<CalendarReminderService>().RunAsync(CancellationToken.None))
            .ConfigureAwait(false);

        return 0;
    }

    private static void ConfigureApplicationPaths(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IApplicationConfiguration>();
        var appData = ApplicationData.Current;
        var releaseIdentity = ReleaseIdentity.Current;

        configuration.ApplicationDataFolderPath = appData.LocalFolder.Path;
        configuration.AllowLegacyDataMigration = releaseIdentity.AllowsLegacyMigration;
        configuration.ApplicationDisplayName = releaseIdentity.DisplayNames["Mail"];
        configuration.PublisherSharedFolderPath = releaseIdentity.AllowsLegacyMigration
            ? appData.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path
            : string.Empty;
        configuration.ApplicationTempFolderPath = appData.TemporaryFolder.Path;
    }

    private static void ConfigureLogging(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IApplicationConfiguration>();
        var logPath = Path.Combine(
            configuration.ApplicationDataFolderPath,
            Constants.ClientLogFile);

        provider.GetRequiredService<IWinoLogger>().SetupLogger(logPath);
    }

    private static FileStream? AcquireInstanceLock()
    {
        var directory = Path.GetDirectoryName(LockPath)!;
        Directory.CreateDirectory(directory);

        try
        {
            return new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
