using System;
using Windows.Storage;

namespace Wino.NotificationHost;

internal static class NotificationHostLogger
{
    private static readonly object SyncRoot = new();

    public static void Write(
        string operation,
        Guid? requestId = null,
        Exception? exception = null,
        string? message = null)
    {
        var line = BuildLine(operation, requestId, exception, message);

        TryWrite(
            Path.Combine(
                GetPackageLocalCachePath(),
                "NotificationHost",
                "Logs",
                "NotificationHost.log"),
            line);

        TryWrite(
            Path.Combine(
                Path.GetTempPath(),
                "Wino Mail",
                "NotificationHost.log"),
            line);
    }

    private static string BuildLine(
        string operation,
        Guid? requestId,
        Exception? exception,
        string? message)
    {
        string appUserModelId;
        try
        {
            appUserModelId = CurrentAppIdentity.GetAppUserModelId();
        }
        catch (Exception ex)
        {
            appUserModelId = $"<AUMID unavailable:{ex.GetType().Name}:0x{ex.HResult:X8}>";
        }

        var details = message ?? exception?.Message ?? string.Empty;
        return $"{DateTimeOffset.UtcNow:O}	{appUserModelId}	{operation}	{requestId?.ToString("N") ?? "-"}	{exception?.GetType().FullName ?? "-"}	0x{exception?.HResult:X8}	{details}{Environment.NewLine}";
    }

    private static string GetPackageLocalCachePath()
    {
        try
        {
            return ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryWrite(string path, string line)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);

            lock (SyncRoot)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never be able to crash or mask the notification host.
        }
    }
}
