using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    private JobObjectHandle? handle;

    private WorkerJobObject(JobObjectHandle handle) => this.handle = handle;

    /// <summary>
    /// Creates a kill-on-close job and assigns the process to it. Returns null when jobs are
    /// unavailable; the caller then supervises with the working-set monitor only.
    /// </summary>
    public static WorkerJobObject? TryAssign(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // Read the process handle before creating the job: a process with no usable
            // handle (never started, already exited) throws InvalidOperationException,
            // which maps to the monitor-only fallback without leaking a job object.
            IntPtr processHandle;
            try
            {
                processHandle = process.Handle;
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                handle.Dispose();
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
                !AssignProcessToJobObject(handle, processHandle))
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
        Interlocked.Exchange(ref handle, null)?.Dispose();
    }

    /// <summary>
    /// Owns the job-object handle. A SafeHandle is safe here despite
    /// <c>KILL_ON_JOB_CLOSE</c>: its finalizer can only run once this object is
    /// unreachable, which implies the sole owner -- held for the whole worker lifetime
    /// and disposed deterministically -- is unreachable too. At that point the worker
    /// is an orphan no supervision path can reach, so kill-on-close reaping it is the
    /// desired outcome, not a premature kill of a live worker.
    /// </summary>
    private sealed class JobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        // Instantiated by the P/Invoke marshaller.
        private JobObjectHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
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

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobObjectHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobObjectHandle job, JobObjectInfoClass infoClass,
        ref JobObjectExtendedLimitInformation info, int size);

    [DllImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobObjectHandle job, IntPtr process);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
