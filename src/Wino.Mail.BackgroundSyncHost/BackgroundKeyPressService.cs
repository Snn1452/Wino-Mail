using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundKeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed() => false;
    public bool IsShiftKeyPressed() => false;
}
