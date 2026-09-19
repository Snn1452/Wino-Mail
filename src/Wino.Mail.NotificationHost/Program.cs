using System;
using Wino.NotificationHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 &&
            !Environment.CommandLine.Contains("----AppNotificationActivated:", StringComparison.OrdinalIgnoreCase))
        {
            return Wino.Mail.NotificationHost.BackgroundHostRuntime.Run();
        }

        return NotificationHostRuntime.Run(args);
    }
}
