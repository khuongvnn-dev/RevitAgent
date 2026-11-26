using System.Runtime.InteropServices;

namespace RevitAgent.Core.Machine;

internal static class MachineNative
{
    public const int SM_REMOTESESSION = 0x1000;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    public const int WTS_CURRENT_SESSION = -1;
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    public const int WTSWinStationName = 1;
    public const int WTSConnectState = 8;
    public const int WTSClientProtocolType = 16;

    public const int WTSActive = 0;
    public const int WTSConnected = 1;
    public const int WTSConnectQuery = 2;
    public const int WTSShadow = 3;
    public const int WTSDisconnected = 4;
    public const int WTSIdle = 5;
    public const int WTSListen = 6;
    public const int WTSReset = 7;
    public const int WTSDown = 8;
    public const int WTSInit = 9;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SwitchDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        int wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);
}
