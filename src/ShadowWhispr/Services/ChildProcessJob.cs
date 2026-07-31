using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShadowWhispr.Services;

/// <summary>
/// Ties the CLI processes ShadowWhispr starts to ShadowWhispr's own lifetime,
/// using a Windows job object set to kill everything in it once the job's last
/// handle closes.
///
/// Cancelling on the way out is not enough on its own. A clean shutdown races
/// it — the app can be gone before the continuation that kills the child ever
/// runs — and a crash or a Task Manager "End task" skips our cleanup entirely.
/// Either way an agent session would carry on acting on the machine with
/// nothing left on screen to stop it. Windows closes the job handle when this
/// process dies however it dies, so the kill cannot be skipped.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    /// <summary>Kill every process in the job when its last handle closes.</summary>
    private const uint LimitKillOnJobClose = 0x2000;

    private const int ExtendedLimitInformation = 9;

    private readonly object _gate = new();
    private nint _handle;
    private bool _disposed;

    /// <summary>The job every AI CLI process is put into.</summary>
    public static ChildProcessJob Shared { get; } = new();

    /// <summary>
    /// Puts a freshly started process into the job. Failures are logged and
    /// swallowed: not being able to guarantee the cleanup is a reason to leave a
    /// note in the log, not a reason to refuse to run the process at all.
    /// </summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (!EnsureJob()) return;
                if (!AssignProcessToJobObject(_handle, process.Handle))
                {
                    AppLog.Write(
                        "Could not tie a CLI process to ShadowWhispr's lifetime " +
                        $"(error {Marshal.GetLastWin32Error()}); it may outlive the app");
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Tying a CLI process to ShadowWhispr's lifetime failed", exception);
        }
    }

    /// <summary>Creates the job on first use. Call under <see cref="_gate"/>.</summary>
    private bool EnsureJob()
    {
        if (_handle != 0) return true;

        var handle = CreateJobObject(0, null);
        if (handle == 0)
        {
            AppLog.Write($"Could not create the child process job (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        var information = new ExtendedLimit
        {
            BasicLimitInformation = new BasicLimit { LimitFlags = LimitKillOnJobClose }
        };

        var size = Marshal.SizeOf<ExtendedLimit>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformation, buffer, (uint)size))
            {
                AppLog.Write($"Could not configure the child process job (error {Marshal.GetLastWin32Error()})");
                CloseHandle(handle);
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _handle = handle;
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle == 0) return;

            // Closing the last handle is what kills everything in the job.
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
        public BasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
