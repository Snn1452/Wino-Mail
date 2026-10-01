using System;
using System.Reflection;

namespace Wino.Mail.BackgroundSyncHost;

internal class HeadlessMailDialogServiceProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new NotSupportedException(
            $"The dialog service method '{targetMethod?.Name ?? "<unknown>"}' is not available in the headless background synchronization host.");
}
