using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RevitAgent.Core.Revit.Native;

/// <summary>
/// Job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: processes assigned to it are killed when the gateway process
/// exits. Used only when revit_launch is called with tie_to_gateway:true (by default Revit outlives the Host).
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    private IntPtr _handle;

    public KillOnCloseJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, ExtendedLimitInformation, ptr, (uint)size))
            {
                throw new InvalidOperationException($"SetInformationJobObject failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Assigns the process; returns false (with the Win32 error) when Windows refuses.</summary>
    public bool TryAssign(Process process, out int win32Error)
    {
        win32Error = 0;
        if (_handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(KillOnCloseJob));
        }

        if (AssignProcessToJobObject(_handle, process.Handle))
        {
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero)
        {
            CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
