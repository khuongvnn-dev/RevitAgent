namespace RevitAgent.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RevitAgent.Host.Automation;
using Xunit;

[Trait("Category", "SpikeS2")]
public class SpikeS2Tests
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] byte[] pvInfo, uint nLength, out uint lpnLengthNeeded);

    [Fact]
    public void DesktopPreflight_IsDesktopAvailable_ReturnsTrueInInteractiveSession()
    {
        // Act
        bool isAvailable = DesktopPreflight.IsDesktopAvailable();
        int win32Err = Marshal.GetLastWin32Error();

        IntPtr threadDesk = GetThreadDesktop(GetCurrentThreadId());
        byte[] nameBuf = new byte[256];
        GetUserObjectInformation(threadDesk, 2 /* UOI_NAME */, nameBuf, 256, out _);
        string deskName = System.Text.Encoding.Unicode.GetString(nameBuf).TrimEnd('\0');

        // When workstation is unlocked and interactive, isAvailable is true.
        // When locked (or in background session), OpenInputDesktop fails with 5 (Access Denied).
        Assert.True(isAvailable || win32Err == 5, $"DeskName: {deskName}, Available: {isAvailable}, LastWin32Error: {win32Err}");
    }

    [Fact]
    public void DesktopPreflight_CheckElevationParity_CurrentProcess_ReturnsTrue()
    {
        // Arrange
        using var currentProc = Process.GetCurrentProcess();

        // Act
        bool parity = DesktopPreflight.CheckElevationParity(currentProc);

        // Assert
        Assert.True(parity, "Current host process must always have elevation parity with itself.");
    }

    [Fact]
    public void DesktopPreflight_CheckElevationParity_NullProcess_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopPreflight.CheckElevationParity(null!));
    }

    [Fact]
    public void DesktopPreflight_IsProcessElevated_NullOrExitedProcess_ReturnsFalseGracefully()
    {
        // Null process
        Assert.False(DesktopPreflight.IsProcessElevated(null!));

        // Exited process
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Assert.NotNull(proc);
        proc.WaitForExit(3000);

        Assert.False(DesktopPreflight.IsProcessElevated(proc));
        proc.Dispose();
    }
}
