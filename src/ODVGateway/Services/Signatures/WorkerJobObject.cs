using System.Runtime.InteropServices;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// A Windows Job Object that owns one worker process. The job exists for one reason: with
/// <c>KILL_ON_JOB_CLOSE</c> a worker can never outlive the gateway's supervision of it, even if
/// the gateway itself is killed. The memory cap is deliberately NOT enforced here: a job-object
/// process-memory limit fails allocations inside the worker, which surfaces as an untimely managed
/// crash rather than the named <c>validation-worker-memory</c> failure. The cap is enforced by the
/// parent's working-set monitor instead, which kills the child deterministically. On non-Windows
/// platforms, and when job assignment fails (for example nested-job conflicts under a CI runner),
/// supervision silently falls back to the monitor alone.
/// </summary>
internal sealed class WorkerJobObject : IDisposable
{
    private IntPtr handle;

    private WorkerJobObject(IntPtr handle) => this.handle = handle;

    /// <summary>
    /// Creates a kill-on-close job and assigns the process to it. Returns null when jobs are
    /// unavailable; the caller then supervises with the working-set monitor only.
    /// </summary>
    public static WorkerJobObject? TryAssign(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var job = new WorkerJobObject(handle);
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitFlags.KillOnJobClose
                }
            };
            if (!SetInformationJobObject(handle, JobObjectInfoClass.ExtendedLimitInformation,
                    ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(handle, process.Handle))
            {
                job.Dispose();
                return null;
            }

            return job;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (current != IntPtr.Zero)
        {
            CloseHandle(current);
        }
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        KillOnJobClose = 0x2000
    }

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, JobObjectInfoClass infoClass,
        ref JobObjectExtendedLimitInformation info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
