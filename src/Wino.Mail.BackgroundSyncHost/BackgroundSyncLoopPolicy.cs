namespace Wino.Mail.BackgroundSyncHost;

/// <summary>
/// Centralizes the decision that prevents the background scheduler from competing
/// with the foreground WinUI runtime.
/// </summary>
internal static class BackgroundSyncLoopPolicy
{
    public static bool ShouldStartLoops(bool isWinUiProcessRunning, bool terminateRequested)
        => !isWinUiProcessRunning && !terminateRequested;

    public static bool ShouldYieldToUi(bool isWinUiProcessRunning)
        => isWinUiProcessRunning;
}
