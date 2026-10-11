using System.Diagnostics;

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

if (args.Length > 0 && args[0] == "owner-crash")
{
    using var child = StartChild();
    Console.WriteLine(child.Id);
    Console.Out.Flush();

    // Simulate abrupt owner termination: bypass managed disposal so the OS closes the job handle.
    _ = Console.ReadLine();
    Environment.Exit(2);
}
else
{
    using var child = StartChild();
    // Returning from the host must close the owning job and terminate any still-running child.
    await Task.Delay(100);
    return 2;
}
