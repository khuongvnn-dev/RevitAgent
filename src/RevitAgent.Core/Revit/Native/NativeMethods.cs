using System.Runtime.InteropServices;
using System.Text;

namespace RevitAgent.Core.Revit.Native;

/// <summary>
/// Win32 interop for process/window observation and precise dialog clicks.
/// Detection only uses calls that never send a message to the target process
/// (EnumWindows, GetClassName, GetWindowText on foreign top-level windows, IsWindowEnabled, IsHungAppWindow...).
/// Text reading uses SendMessageTimeout so a busy target can never hang the gateway.
/// </summary>
internal static class NativeMethods
{
    internal const uint GwOwner = 4;
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;

    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExTopmost = 0x00000008;
    internal const long WsDisabled = 0x08000000;
    internal const long WsCaption = 0x00C00000;

    internal const uint WmNull = 0x0000;
    internal const uint WmClose = 0x0010;
    internal const uint WmGetText = 0x000D;
    internal const uint WmGetTextLength = 0x000E;
    internal const uint WmCommand = 0x0111;
    internal const uint BmClick = 0x00F5;
    internal const uint WmUser = 0x0400;

    /// <summary>TaskDialog message: simulate a click on the button with the given id (wParam).</summary>
    internal const uint TdmClickButton = WmUser + 102;

    internal const uint SmtoBlock = 0x0001;
    internal const uint SmtoAbortIfHung = 0x0002;

    internal const int BnClicked = 0;

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetLastActivePopup(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetDlgCtrlID(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg, IntPtr wParam, char[] lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    internal static long GetWindowLong(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index).ToInt64() : GetWindowLong32(hWnd, index);

    internal static IntPtr MakeWParam(int low, int high) => (IntPtr)((high << 16) | (low & 0xFFFF));
}
