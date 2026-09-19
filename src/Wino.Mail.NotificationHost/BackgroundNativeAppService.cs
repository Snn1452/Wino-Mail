using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.NotificationHost;

internal sealed class BackgroundNativeAppService : INativeAppService
{
    public Func<IntPtr> GetCoreWindowHwnd { get; set; } = static () => IntPtr.Zero;
    public string GetWebAuthenticationBrokerUri() => string.Empty;
    public Task<string> GetMimeMessageStoragePath()
        => Task.FromResult(System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Mime"));
    public Task LaunchFileAsync(string filePath) => Task.CompletedTask;
    public Task<bool> LaunchUriAsync(Uri uri) => Task.FromResult(false);
    public bool IsAppRunning() => false;
    public string GetFullAppVersion()
    {
        var v = Windows.ApplicationModel.Package.Current.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }
    public Task PinAppToTaskbarAsync() => Task.CompletedTask;
    public Wino.Core.Domain.Enums.WindowsTaskbarPosition GetTaskbarPosition() => Wino.Core.Domain.Enums.WindowsTaskbarPosition.Bottom;
    public string GetCalendarAttachmentsFolderPath()
        => System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "CalendarAttachments");
}
