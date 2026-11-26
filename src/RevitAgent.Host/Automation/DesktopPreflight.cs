namespace RevitAgent.Host.Automation;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

/// <summary>
/// Preflight validator for desktop session availability and UAC elevation parity.
/// </summary>
public static class DesktopPreflight
{
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        IntPtr TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    /// <summary>
    /// Checks whether an interactive input desktop is currently available.
    /// Returns false if the workstation is locked, a screen saver is active,
    /// or the session is running non-interactively.
    /// </summary>
    public static bool IsDesktopAvailable()
    {
        try
        {
            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (hDesktop == IntPtr.Zero)
            {
                return false;
            }

            CloseDesktop(hDesktop);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if elevation parity holds between the host and the target process.
    /// Parity holds if the host is running as Administrator OR the target process is not elevated.
    /// </summary>
    public static bool CheckElevationParity(Process targetProcess)
    {
        ArgumentNullException.ThrowIfNull(targetProcess);

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        bool isHostElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

        return isHostElevated || !IsProcessElevated(targetProcess);
    }

    /// <summary>
    /// Determines whether the specified process is running with elevated privileges.
    /// Handles exceptions gracefully and falls back to false.
    /// </summary>
    public static bool IsProcessElevated(Process process)
    {
        if (process == null) return false;

        IntPtr tokenHandle = IntPtr.Zero;
        IntPtr elevationPtr = IntPtr.Zero;
        try
        {
            if (process.HasExited) return false;

            if (!OpenProcessToken(process.Handle, TOKEN_QUERY, out tokenHandle))
            {
                return false;
            }

            int elevationSize = Marshal.SizeOf<TOKEN_ELEVATION>();
            elevationPtr = Marshal.AllocHGlobal(elevationSize);

            if (GetTokenInformation(tokenHandle, TokenElevation, elevationPtr, (uint)elevationSize, out _))
            {
                var elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(elevationPtr);
                return elevation.TokenIsElevated != 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (elevationPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(elevationPtr);
            }
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }
        }
    }
}
