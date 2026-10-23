using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

/// <summary>Supplies keyboard-shortcut metadata without consulting the WinUI process.</summary>
internal sealed class BackgroundShortcutPlatformService : IShortcutPlatformService
{
    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Control;

    public bool IsShiftKeyPressed() => false;
}
