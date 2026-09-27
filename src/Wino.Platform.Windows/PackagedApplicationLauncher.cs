using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Wino.Platform.Windows;

public static class PackagedApplicationLauncher
{
    private static readonly Guid ApplicationActivationManagerClassId = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ApplicationActivationManagerInterfaceId = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");

    public static Task<uint> LaunchAsync(string appUserModelId, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        arguments ??= string.Empty;

        return Task.Run(() => LaunchCore(appUserModelId, arguments));
    }

    private static unsafe uint LaunchCore(string appUserModelId, string arguments)
    {
        var classId = ApplicationActivationManagerClassId;
        var interfaceId = ApplicationActivationManagerInterfaceId;
        var result = CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            5,
            ref interfaceId,
            out var instance);

        Marshal.ThrowExceptionForHR(result);

        try
        {
            var virtualTable = *(void***)instance;
            var activateApplication =
                (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, uint, uint*, int>)virtualTable[3];

            fixed (char* appUserModelIdPointer = appUserModelId)
            fixed (char* argumentsPointer = arguments)
            {
                uint processId = 0;
                result = activateApplication(
                    instance,
                    appUserModelIdPointer,
                    argumentsPointer,
                    0,
                    &processId);

                Marshal.ThrowExceptionForHR(result);
                return processId;
            }
        }
        finally
        {
            _ = Marshal.Release(instance);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        out IntPtr instance);
}
