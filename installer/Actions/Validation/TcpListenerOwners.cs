using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace RTXLens.Installer.Actions.Validation;

public static class TcpListenerOwners
{
    public static IEnumerable<int> Read(int port)
    {
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);

            if (result != 0 && result != 122)
            {
                throw new Win32Exception(result);
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var allocated = size;
                var buffer = Marshal.AllocHGlobal(allocated);

                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (result == 122)
                    {
                        continue;
                    }

                    if (result != 0)
                    {
                        throw new Win32Exception(result);
                    }

                    var rowSize = family == 2 ? 24 : 56;
                    var count = Marshal.ReadInt32(buffer);

                    if (count < 0 || count > (allocated - 4) / rowSize)
                    {
                        throw new InvalidOperationException("Invalid Windows TCP listener table.");
                    }

                    for (var row = 0; row < count; row++)
                    {
                        var offset = 4 + (row * rowSize);
                        var value = (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(buffer, offset + (family == 2 ? 8 : 20)));

                        if (value == port)
                        {
                            yield return Marshal.ReadInt32(buffer, offset + (family == 2 ? 20 : 52));
                        }
                    }

                    break;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            if (result != 0)
            {
                throw new Win32Exception(result);
            }
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort, int family, int tableClass, int reserved);
}
