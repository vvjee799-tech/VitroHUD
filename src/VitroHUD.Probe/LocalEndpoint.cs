using System.Runtime.InteropServices;

internal static class LocalEndpoint
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    // Bind a discovered debug port to the same original window's process.
    // A stale profile file must never redirect commands to some other listener.
    internal static bool OwnedBy(int port, int processId)
    {
        if (processId <= 0) return false;
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, 2, 3, 0); // IPv4, TCP_TABLE_OWNER_PID_LISTENER
        if (size < 4 || size > 4_000_000) return false;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return false;
            var count = Marshal.ReadInt32(table);
            if (count < 0 || count > (size - 4) / 24) return false;
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * 24;
                var encoded = Marshal.ReadInt32(row, 8);
                var rowPort = ((encoded & 255) << 8) | ((encoded >> 8) & 255);
                if (rowPort == port && Marshal.ReadInt32(row, 20) == processId
                    && Marshal.ReadInt32(row, 4) == 0x0100007f) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }
}
