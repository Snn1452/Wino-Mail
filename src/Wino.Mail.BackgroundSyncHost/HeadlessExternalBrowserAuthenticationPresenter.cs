using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class HeadlessExternalBrowserAuthenticationPresenter : IExternalBrowserAuthenticationPresenter
{
    public Task<IExternalBrowserAuthenticationSession> ShowAsync(
        ExternalBrowserAuthenticationRequest request,
        Action cancelRequested)
        => Task.FromResult<IExternalBrowserAuthenticationSession>(
            HeadlessSession.Instance);

    private sealed class HeadlessSession : IExternalBrowserAuthenticationSession
    {
        public static readonly HeadlessSession Instance = new();

        public Task NotifyBrowserLaunchFailedAsync()
            => Task.CompletedTask;

        public Task NotifyRedirectReceivedAsync()
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
