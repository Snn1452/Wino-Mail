using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Mail.BackgroundSyncHost;

/// <summary>
/// Prevents background synchronization from trying to open UI-owned files or browsers.
/// Authentication continues only with credentials already available to the host.
/// </summary>
internal sealed class BackgroundExternalLauncher : IExternalLauncher
{
    public Task<PlatformOperationResult> LaunchFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlatformOperationResult(
            PlatformOperationStatus.Unavailable,
            "File activation is unavailable in the headless background host."));
    }

    public Task<PlatformOperationResult> LaunchUriAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlatformOperationResult(
            PlatformOperationStatus.Unavailable,
            "Browser activation is unavailable in the headless background host."));
    }
}
