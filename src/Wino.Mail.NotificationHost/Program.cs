using System;
using Windows.ApplicationModel;
using Wino.NotificationHost;
using Wino.NotificationHost.Contracts;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (IsBackgroundHost())
        {
            var package = Package.Current;
            ReleaseIdentity.Initialize(
                package.InstalledLocation.Path,
                package.Id.Name,
                package.Id.Publisher,
                package.Id.FamilyName);

            return BackgroundHostRuntime.Run();
        }

        return NotificationHostRuntime.Run(args);
    }

    private static bool IsBackgroundHost()
        => CurrentAppIdentity.GetAppUserModelId().EndsWith(
            "!" + NotificationHostApplicationIds.Background,
            StringComparison.Ordinal);
}