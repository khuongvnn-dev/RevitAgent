using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>Preconditions of desktop UI automation: an unlocked interactive desktop and no UIPI barrier.</summary>
public static class UiEnvironment
{
    public const string LockedMessage = "Windows session is locked; UI automation needs an unlocked interactive desktop.";

    private static readonly object Gate = new();
    private static DateTime _checkedAt = DateTime.MinValue;
    private static string? _lastReason;
    private static bool? _selfElevated;

    /// <summary>
    /// Null when the desktop is usable, otherwise the reason. "Locked" = LogonUI runs in this process's session
    /// (a LogonUI in another session, e.g. the console while connected over RDP, does not count) or the input
    /// desktop cannot be opened (locked, UAC secure desktop, or a non-interactive service session).
    /// </summary>
    public static string? DesktopUnavailableReason()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _checkedAt < TimeSpan.FromMilliseconds(750))
            {
                return _lastReason;
            }
        }

        string? reason = null;
        var desk = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DesktopSwitchDesktop);
        if (desk == IntPtr.Zero)
        {
            reason = LockedMessage + " (the input desktop cannot be opened: locked screen, UAC prompt or a non-interactive session).";
        }
        else
        {
            NativeMethods.CloseDesktop(desk);
            if (IsLogonUiInMySession())
            {
                reason = LockedMessage + " (LogonUI is running in this session).";
            }
        }

        lock (Gate)
        {
            _checkedAt = DateTime.UtcNow;
            _lastReason = reason;
        }

        return reason;
    }

    /// <summary>Throws DEPENDENCY_FAILED when the session is locked / not interactive.</summary>
    public static void EnsureDesktop()
    {
        var reason = DesktopUnavailableReason();
        if (reason is not null)
        {
            throw new GatewayException(GatewayErrorCodes.DependencyFailed, reason, new { remedy = "Unlock the Windows session (or keep the RDP session connected) and retry." });
        }
    }

    public static bool IsSelfElevated()
    {
        lock (Gate)
        {
            if (_selfElevated is { } v)
            {
                return v;
            }
        }

        bool elevated;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            elevated = false;
        }

        lock (Gate)
        {
            _selfElevated = elevated;
        }

        return elevated;
    }

    /// <summary>
    /// True when the target process runs elevated (or its token is unreadable from this non-elevated process, which for
    /// a desktop app of the same user means elevated). Windows UIPI then blocks input and most UI Automation calls.
    /// </summary>
    public static bool IsTargetElevatedAboveUs(int pid)
    {
        if (IsSelfElevated())
        {
            return false;
        }

        var h = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() == NativeMethods.ErrorAccessDenied;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(h, NativeMethods.TokenQuery, out var token))
            {
                return Marshal.GetLastWin32Error() == NativeMethods.ErrorAccessDenied;
            }

            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>Throws PERMISSION_DENIED when UIPI would block automation of <paramref name="pid"/>.</summary>
    public static void EnsureNotElevatedAboveUs(int pid)
    {
        if (IsTargetElevatedAboveUs(pid))
        {
            throw GatewayException.Denied(
                $"Process {pid} runs elevated (as Administrator) but the gateway does not. Windows UIPI blocks UI automation and input into it. " +
                "Start Revit without 'Run as administrator', or run the RevitAgent Host elevated too.");
        }
    }

    private static bool IsLogonUiInMySession()
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName("LogonUI");
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            if (procs.Length == 0)
            {
                return false;
            }

            int mine;
            using (var me = Process.GetCurrentProcess())
            {
                mine = me.SessionId;
            }

            return procs.Any(p =>
            {
                try
                {
                    return p.SessionId == mine;
                }
                catch (Exception)
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }
}
