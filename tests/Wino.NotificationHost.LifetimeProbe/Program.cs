using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static Process StartChild()
{
    var info = new ProcessStartInfo(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe"))
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };
    info.ArgumentList.Add("-NoProfile");
    info.ArgumentList.Add("-NonInteractive");
    info.ArgumentList.Add("-Command");
    info.ArgumentList.Add("Start-Sleep -Seconds 60");
    return Process.Start(info) ?? throw new InvalidOperationException("Could not start lifetime probe child.");
}

using var job = new KillOnCloseJob();
using var child = StartChild();
job.Assign(child.Id);

if (args.Length > 0 && args[0] == "owner-crash")
{
    Console.WriteLine(child.Id);
    Console.Out.Flush();

    // Simulate abrupt owner termination: bypass managed disposal so the OS closes the job handle.
    _ = Console.ReadLine();
    Environment.Exit(2);
}
else
{
    // Returning from the host closes the owning job and terminates any still-running child.
    await Task.Delay(100);
    Environment.ExitCode = 2;
}

internal sealed class KillOnCloseJob : IDisposable
{
    private readonly SafeJobHandle _job;

    public KillOnCloseJob()
    {
        _job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (_job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var limits = new JobExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0x2000;
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Assign(int processId)
    {
        using var process = OpenProcess(0x0101, false, (uint)processId);
        if (process.IsInvalid || !AssignProcessToJobObject(_job, process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign lifetime probe child to its job.");
    }

    public void Dispose() => _job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass,
        ref JobExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
