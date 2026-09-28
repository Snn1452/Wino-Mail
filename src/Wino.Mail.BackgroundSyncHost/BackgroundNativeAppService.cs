using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundNativeAppService : INativeAppService
{
    public Func<IntPtr> GetCoreWindowHwnd { get; set; } = static () => IntPtr.Zero;

    public string GetWebAuthenticationBrokerUri() => string.Empty;

    public Task<string> GetMimeMessageStoragePath()
        => Task.FromResult(Path.Combine(ApplicationData.Current.LocalFolder.Path, "Mime"));

    public Task LaunchFileAsync(string filePath)
        => throw new NotSupportedException("File activation is not available from the headless background host.");

    public Task<bool> LaunchUriAsync(Uri uri)
        => Task.FromResult(false);

    public Task CopyClipboardAsync(string text)
        => Task.CompletedTask;

    public bool IsCtrlKeyPressed() => false;

    public bool IsShiftKeyPressed() => false;

    public Task<StartupBehaviorResult> GetCurrentStartupBehaviorAsync()
        => Task.FromResult(StartupBehaviorResult.Disabled);

    public Task<StartupBehaviorResult> ToggleStartupBehavior(bool isEnabled)
        => Task.FromResult(StartupBehaviorResult.Unknown);

    public Task<bool> IsWebView2RuntimeAvailableAsync()
        => Task.FromResult(false);

    public void PlayTaskCompletionSound()
    {
    }

    public bool IsAppRunning() => false;

    public string GetFullAppVersion()
    {
        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    public Task PinAppToTaskbarAsync()
        => Task.CompletedTask;

    public WindowsTaskbarPosition GetTaskbarPosition()
        => WindowsTaskbarPosition.Bottom;

    public string GetCalendarAttachmentsFolderPath()
        => Path.Combine(ApplicationData.Current.LocalFolder.Path, "CalendarAttachments");
}
