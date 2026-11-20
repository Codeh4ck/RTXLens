using RTXLens.Contracts.Monitoring;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RTXLens.Installer.Actions.Services;

public static class ServiceStartupConfiguration
{
    private static int[] _settings = [3, 4];

    public static int RunningProcessId()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(manager, WindowsServiceIdentity.Name, 4);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == 1060 ? 0 : throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var status = new byte[36];
                if (!QueryServiceStatusEx(service, 0, status, status.Length, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return BitConverter.ToInt32(status, 4) == 4 ? BitConverter.ToInt32(status, 28) : 0;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static void Apply()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(manager, WindowsServiceIdentity.Name, 2);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var enabled = 1;
                foreach (var setting in _settings)
                {
                    if (!ChangeServiceConfig2(service, setting, ref enabled))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, int access);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, int level, ref int value);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, [Out] byte[] buffer, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
