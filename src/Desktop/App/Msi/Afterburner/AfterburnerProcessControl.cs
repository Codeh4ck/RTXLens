using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RTXLens.Presentation.Msi.Afterburner;

namespace RTXLens.App.Msi.Afterburner;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class AfterburnerProcessControl
{
    internal static async Task CloseAsync(string installation)
    {
        var expectedExecutable = Path.Combine(installation, "MSIAfterburner.exe");
        var processes = Process.GetProcessesByName("MSIAfterburner");
        using var current = Process.GetCurrentProcess();

        try
        {
            // Validate every target before requesting any exits. A different installation/session is never stopped.
            foreach (var process in processes)
            {
                if (process.HasExited)
                {
                    continue;
                }

                if (process.SessionId != current.SessionId
                    || !string.Equals(process.MainModule?.FileName, expectedExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Another Afterburner installation or Windows session is running. Close it manually before restarting this installation.");
                }
            }

            foreach (var process in processes)
            {
                if (process.HasExited)
                {
                    continue;
                }

                var window = process.MainWindowHandle;
                if (window == 0)
                {
                    // The tray application can have a hidden main window; Process.MainWindowHandle excludes it.
                    EnumWindows((candidate, _) =>
                    {
                        GetWindowThreadProcessId(candidate, out var owner);
                        if (owner != (uint)process.Id)
                        {
                            return true;
                        }

                        var title = new StringBuilder(256);
                        GetWindowTextW(candidate, title, title.Capacity);
                        if (!title.ToString().StartsWith("MSI Afterburner", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        window = candidate;
                        return false;
                    }, 0);
                }

                if (window == 0)
                {
                    throw new InvalidOperationException("Afterburner's main window was not found. Exit Afterburner from its tray icon, then click Restart Afterburner.");
                }

                if (!PostMessageW(window, 0x0010, 0, 0)) // WM_CLOSE: ask for a normal exit, never terminate the process.
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Afterburner did not accept the exit request. Close it from its tray icon before restarting.");
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(timeout.Token)));
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Afterburner has not exited. Close it from its tray icon, then click Restart Afterburner. No process was forcibly terminated.");
            }

            AfterburnerDiscovery.RequireStopped();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static bool HasElevatedInstance(string installation)
    {
        var expected = Path.Combine(installation, "MSIAfterburner.exe");
        var processes = Process.GetProcessesByName("MSIAfterburner");
        using var current = Process.GetCurrentProcess();
        try
        {
            foreach (var process in processes)
            {
                if (process.SessionId != current.SessionId
                    || !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!OpenProcessToken(process.Handle, 8, out var token))
                {
                    return false;
                }

                using (token)
                {
                    return GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated != 0;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int information, int size, out int returnedSize);

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}