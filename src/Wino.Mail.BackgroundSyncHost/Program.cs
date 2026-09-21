using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SQLite;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
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
        WriteDiagnostic($"Process entered. BaseDirectory={AppContext.BaseDirectory}");

        while (true)
        {
            try
            {
                using var instanceLock = AcquireInstanceLock();
                if (instanceLock is null)
                {
                    WriteDiagnostic("Another background host instance is already running.");
                    return 0;
                }

                WriteDiagnostic("Instance lock acquired.");
                return RunWithRecoveryAsync().GetAwaiter().GetResult();
            }
            catch (IOException ex)
            {
                WriteDiagnostic($"Background host lock/storage startup failed: {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                WriteDiagnostic($"Background host startup access was denied: {ex}");
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Fatal error: {ex}");
                try
                {
                    Serilog.Log.Error(ex, "Background synchronization host failed.");
                }
                catch
                {
                }

                return 1;
            }

            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<int> RunWithRecoveryAsync()
    {
        var retryDelay = TimeSpan.FromSeconds(30);

        while (true)
        {
            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                WriteDiagnostic("COM wrappers initialized.");

                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                WriteDiagnostic("Code pages encoding provider initialized.");

                InitializeReleaseIdentity();
                return await RunAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Background host run failed: {ex}");

                try
                {
                    Serilog.Log.Error(ex, "Background synchronization host run failed.");
                }
                catch
                {
                }

                try
                {
                    var preferences = new PreferencesService(new ConfigurationService(useCache: false));
                    if (!IsBackgroundSyncEnabled(preferences.AppCloseBehavior))
                        return 0;
                }
                catch
                {
                    // Keep retrying when preferences cannot yet be read. This commonly occurs while
                    // packaged app infrastructure is still settling after login.
                }

                await Task.Delay(retryDelay).ConfigureAwait(false);
            }
        }
    }

    private static void InitializeReleaseIdentity()
    {
        var package = Package.Current;
        ReleaseIdentity.Initialize(
            package.InstalledLocation.Path,
            package.Id.Name,
            package.Id.Publisher,
            package.Id.FamilyName);

        WriteDiagnostic("Release identity initialized.");
    }

    private static async Task<int> RunAsync()
    {
        WriteDiagnostic("Starting RunAsync.");

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

        WriteDiagnostic("Service provider built.");
        ConfigureApplicationPaths(provider);
        WriteDiagnostic("Application paths configured.");

        ConfigureLogging(provider);
        WriteDiagnostic("Logging configured.");

        var migrationPlan = await RetryStartupStepAsync(
                "Migration inspection",
                () => provider.GetRequiredService<IMigrationCoordinator>().InspectAsync())
            .ConfigureAwait(false);

        WriteDiagnostic($"Migration inspection completed: {migrationPlan.Status}.");

        if (migrationPlan.Status != Wino.Core.Domain.Models.Migration.MigrationStatus.NotRequired)
        {
            Serilog.Log.Information(
                "Background synchronization host is idle because database migration state is {MigrationStatus}.",
                migrationPlan.Status);
            return 0;
        }

        var preferences = provider.GetRequiredService<IPreferencesService>();
        if (!IsBackgroundSyncEnabled(preferences.AppCloseBehavior))
        {
            Serilog.Log.Information(
                "Background synchronization host is disabled because AppCloseBehavior is {AppCloseBehavior}.",
                preferences.AppCloseBehavior);
            return 0;
        }

        WriteDiagnostic("Background mode enabled.");
        WriteDiagnostic("Initializing database.");

        await RetryStartupStepAsync(
                "Database initialization",
                async () =>
                {
                    await provider.GetRequiredService<IDatabaseService>()
                        .InitializeAsync()
                        .ConfigureAwait(false);
                    return true;
                })
            .ConfigureAwait(false);

        WriteDiagnostic("Database initialized.");

        var accountService = provider.GetRequiredService<IAccountService>();
        if (!(await accountService.GetAccountsAsync().ConfigureAwait(false)).Any())
        {
            Serilog.Log.Information("Background synchronization host is exiting because no accounts are configured.");
            return 0;
        }

        WriteDiagnostic("Account service ready.");

        await RetryStartupStepAsync(
                "Translation initialization",
                async () =>
                {
                    await provider.GetRequiredService<ITranslationService>()
                        .InitializeAsync()
                        .ConfigureAwait(false);
                    return true;
                })
            .ConfigureAwait(false);

        WriteDiagnostic("Translations initialized.");

        await RetryStartupStepAsync(
                "Synchronization manager initialization",
                async () =>
                {
                    await provider.GetRequiredService<SynchronizationManagerInitializer>()
                        .InitializeAsync()
                        .ConfigureAwait(false);
                    return true;
                })
            .ConfigureAwait(false);

        WriteDiagnostic("Synchronization manager initialized.");

        using var hostCts = new CancellationTokenSource();

        var synchronizationTask = provider
            .GetRequiredService<AutoSynchronizationService>()
            .RunAsync(hostCts.Token);
        var reminderTask = provider
            .GetRequiredService<CalendarReminderService>()
            .RunAsync(hostCts.Token);
        var lifetimeMonitorTask = MonitorHostLifetimeAsync(
            preferences,
            accountService,
            hostCts);

        try
        {
            await Task.WhenAll(
                    synchronizationTask,
                    reminderTask,
                    lifetimeMonitorTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hostCts.IsCancellationRequested)
        {
            Serilog.Log.Information("Background synchronization host is stopping.");
        }

        return 0;
    }

    private static bool IsBackgroundSyncEnabled(AppCloseBehavior behavior)
        => behavior is AppCloseBehavior.RunInBackgroundWithTrayIcon
            or AppCloseBehavior.RunInBackgroundWithoutTrayIcon;

    private static async Task MonitorHostLifetimeAsync(
        IPreferencesService preferences,
        IAccountService accountService,
        CancellationTokenSource hostCts)
    {
        var consecutiveEmptyAccountChecks = 0;

        try
        {
            while (!hostCts.IsCancellationRequested)
            {
                if (!IsBackgroundSyncEnabled(preferences.AppCloseBehavior))
                    break;

                try
                {
                    var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);

                    if (accounts.Count == 0)
                    {
                        consecutiveEmptyAccountChecks++;

                        if (consecutiveEmptyAccountChecks >= 3)
                            break;
                    }
                    else
                    {
                        consecutiveEmptyAccountChecks = 0;
                    }
                }
                catch (Exception ex)
                {
                    consecutiveEmptyAccountChecks = 0;
                    Serilog.Log.Warning(ex, "Background host account-presence check failed; keeping host alive.");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), hostCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (hostCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (!hostCts.IsCancellationRequested)
                hostCts.Cancel();
        }
    }

    private static async Task<T> RetryStartupStepAsync<T>(
        string operationName,
        Func<Task<T>> operation)
    {
        var delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30)
        };

        Exception? lastException = null;

        foreach (var delay in delays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay).ConfigureAwait(false);

            try
            {
                WriteDiagnostic($"Starting {operationName}.");
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SQLiteException or System.ComponentModel.Win32Exception)
            {
                lastException = ex;
                WriteDiagnostic($"{operationName} failed and will be retried: {ex.GetType().Name}: {ex.Message}");
                Serilog.Log.Warning(ex, "{OperationName} failed during background host startup; retrying.", operationName);
            }
        }

        throw new InvalidOperationException(
            $"Background synchronization host could not complete {operationName} after multiple attempts.",
            lastException);
    }

    private static void ConfigureApplicationPaths(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<ApplicationConfiguration>();
        var appData = ApplicationData.Current;
        var releaseIdentity = ReleaseIdentity.Current;

        configuration.ApplicationDataFolderPath = appData.LocalFolder.Path;
        configuration.PublisherSharedFolderPath = releaseIdentity.AllowsLegacyMigration
            ? appData.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path
            : string.Empty;
        configuration.ApplicationTempFolderPath = appData.TemporaryFolder.Path;

        if (configuration is ApplicationConfiguration concreteConfiguration)
        {
            concreteConfiguration.AllowLegacyDataMigration = releaseIdentity.AllowsLegacyMigration;
            concreteConfiguration.ApplicationDisplayName = releaseIdentity.DisplayNames["Mail"];
        }
    }

    private static void ConfigureLogging(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<ApplicationConfiguration>();
        var logPath = Path.Combine(
            configuration.ApplicationDataFolderPath,
            "BackgroundSyncHost.log");

        provider.GetRequiredService<IWinoLogger>().SetupLogger(logPath);
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wino Mail");

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "BackgroundSyncHost.startup.log");
            File.AppendAllText(
                path,
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never prevent the host from starting or stopping.
        }
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
