using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.NotificationHost;

internal sealed class BackgroundPresenceStateProvider : IUserPresenceStateProvider
{
    public bool IsPresenting() => false;
}
