using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;
using Windows.ApplicationModel;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.NotificationHost.Contracts;
using Wino.Platform.Windows.Services;
using Wino.Services;
namespace Wino.Mail.BackgroundSyncHost;
internal static class Program
{
    private static readonly string LockPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Wino Mail","background-sync-host.lock");
    [STAThread] private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            ReleaseIdentity.Initialize(Package.Current.InstalledLocation.Path,Package.Current.Id.Name,Package.Current.Id.Publisher,Package.Current.Id.FamilyName);
            using var instanceLock=AcquireInstanceLock(); if(instanceLock==null)return 0;
            var services=new ServiceCollection();services.AddLogging(b=>b.SetMinimumLevel(LogLevel.None));services.RegisterSharedServices();services.RegisterCoreServices();
            services.AddSingleton<IConfigurationService>(_=>new ConfigurationService(false));
            services.AddSingleton<IPreferencesService,PreferencesService>();
            services.AddSingleton<IUserPresenceStateProvider,ShellUserPresenceStateProvider>();
            services.AddSingleton<IAppMetadataService,BackgroundAppMetadataService>();
            services.AddSingleton<IStatePersistanceService,BackgroundStatePersistenceService>();
            services.AddSingleton<BackgroundNotificationHostClient>();
            services.AddSingleton<INotificationBuilder,HeadlessNotificationBuilder>();
            services.AddSingleton<AutoSynchronizationService>();services.AddSingleton<CalendarReminderService>();
            using var sp=services.BuildServiceProvider();SentrySdk.Init(o=>o.Dsn=sp.GetRequiredService<IApplicationConfiguration>().SentryDNS);
            sp.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync().GetAwaiter().GetResult();
            return Task.WhenAll(sp.GetRequiredService<AutoSynchronizationService>().RunAsync(CancellationToken.None),sp.GetRequiredService<CalendarReminderService>().RunAsync(CancellationToken.None)).GetAwaiter().GetResult()==null?0:0;
        }
        catch(Exception ex){Serilog.Log.Error(ex,"Background synchronization host failed.");return 1;}
    }
    private static FileStream? AcquireInstanceLock(){var d=Path.GetDirectoryName(LockPath)!;Directory.CreateDirectory(d);try{return new FileStream(LockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch{return null;}}
}