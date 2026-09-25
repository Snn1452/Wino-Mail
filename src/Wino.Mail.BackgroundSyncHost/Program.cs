using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.NotificationHost.Contracts;
using Wino.Mail.WinUI.Services;
using Wino.Services;

namespace Wino.Mail.BackgroundSyncHost;

internal static class Program
{
    private static readonly string LockPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wino Mail",
        "background-sync-host.lock");

    private static readonly string StartupDiagnosticPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wino Mail",
        "background-sync-host-startup.log");

    [STAThread]
    private static int Main(string[] args)
    {
        var stage = "WinRT initialization";

        try
        {
            WriteStartupDiagnostic("START", stage);

            WinRT.ComWrappersSupport.InitializeComWrappers();

            stage = "package identity";
            var package = Package.Current;
            WriteStartupDiagnostic(
                "PACKAGE",
                $"Name={package.Id.Name}; Publisher={package.Id.Publisher}; Family={package.Id.FamilyName}; Location={package.InstalledLocation.Path}");

            stage = "release identity";
            ReleaseIdentity.Initialize(
                package.InstalledLocation.Path,
                package.Id.Name,
                package.Id.Publisher,
                package.Id.FamilyName);

            if (string.Equals(
                    Environment.GetEnvironmentVariable("WINO_BACKGROUND_SYNC_HOST_SMOKE_TEST"),
                    "1",
                    StringComparison.Ordinal))
            {
                WriteStartupDiagnostic("SMOKE", "Release identity initialized successfully.");
                return 0;
            }

            stage = "instance lock";
            using var instanceLock = AcquireInstanceLock();
            if (instanceLock is null)
            {
                WriteStartupDiagnostic("LOCK", "Another background synchronization host instance is already running.");
                return 0;
            }

            WriteStartupDiagnostic("STARTING", "Dependency initialization starting.");
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteStartupDiagnostic("FAIL", $"{stage}: {ex}");
            try
            {
                Serilog.Log.Error(ex, "Background synchronization host failed during {Stage}.", stage);
            }
            catch
            {
                // Logging may not have been initialized yet; the startup diagnostic file is the fallback.
            }

            return 1;
        }
    }

    private static void WriteStartupDiagnostic(string state, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(StartupDiagnosticPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                StartupDiagnosticPath,
                $"{DateTimeOffset.UtcNow:O} [{state}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never become the reason startup fails.
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
        WriteStartupDiagnostic("SERVICES", "Registering shared and core services.");
        services.AddSingleton<CalendarReminderService>();

        WriteStartupDiagnostic("SERVICES", "Building dependency injection provider.");
        await using var provider = services.BuildServiceProvider();

        WriteStartupDiagnostic("PATHS", "Configuring application paths.");
        ConfigureApplicationPaths(provider);

        WriteStartupDiagnostic("LOGGING", "Configuring structured logging.");
        ConfigureLogging(provider);

        WriteStartupDiagnostic("PREFERENCES", "Reading AppCloseBehavior.");
        var closeBehavior = provider.GetRequiredService<IPreferencesService>().AppCloseBehavior;
        WriteStartupDiagnostic("PREFERENCES", $"AppCloseBehavior={closeBehavior}");
        if (closeBehavior == AppCloseBehavior.Terminate)
        {
            WriteStartupDiagnostic("EXIT", "Background synchronization is disabled by AppCloseBehavior.Terminate.");
            return 0;
        }

        WriteStartupDiagnostic("MIGRATION", "Inspecting migration state.");
        var migrationPlan = await provider.GetRequiredService<IMigrationCoordinator>()
            .InspectAsync()
            .ConfigureAwait(false);
        WriteStartupDiagnostic("MIGRATION", $"Status={migrationPlan.Status}");

        if (migrationPlan.Status != Wino.Core.Domain.Models.Migration.MigrationStatus.NotRequired)
        {
            Serilog.Log.Information(
                "Background synchronization host is idle because migration status is {MigrationStatus}.",
                migrationPlan.Status);
            WriteStartupDiagnostic("EXIT", $"Migration status is {migrationPlan.Status}; host is idle.");
            return 0;
        }

        WriteStartupDiagnostic("DATABASE", "Initializing database.");
        await provider.GetRequiredService<IDatabaseService>()
            .InitializeAsync()
            .ConfigureAwait(false);

        WriteStartupDiagnostic("TRANSLATIONS", "Initializing translations.");
        await provider.GetRequiredService<ITranslationService>()
            .InitializeAsync()
            .ConfigureAwait(false);

        WriteStartupDiagnostic("SYNC", "Initializing SynchronizationManager.");
        await provider.GetRequiredService<SynchronizationManagerInitializer>()
            .InitializeAsync()
            .ConfigureAwait(false);

        WriteStartupDiagnostic("LOOPS", "Starting automatic synchronization and calendar reminder loops.");
        await Task.WhenAll(
            provider.GetRequiredService<AutoSynchronizationService>().RunAsync(CancellationToken.None),
            provider.GetRequiredService<CalendarReminderService>().RunAsync(CancellationToken.None))
            .ConfigureAwait(false);

        WriteStartupDiagnostic("EXIT", "Background synchronization host loops completed.");
        return 0;
    }

    private static void ConfigureApplicationPaths(IServiceProvider provider)
    {
        var configuration = (ApplicationConfiguration)provider.GetRequiredService<IApplicationConfiguration>();
        var appData = ApplicationData.Current;
        var releaseIdentity = ReleaseIdentity.Current;

        configuration.ApplicationDataFolderPath = appData.LocalFolder.Path;
        configuration.AllowLegacyDataMigration = releaseIdentity.AllowsLegacyMigration;
        configuration.ApplicationDisplayName = releaseIdentity.DisplayNames["Mail"];
        configuration.PublisherSharedFolderPath = releaseIdentity.AllowsLegacyMigration
            ? appData.GetPublisherCacheFolder(Wino.Services.ApplicationConfiguration.SharedFolderName).Path
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
