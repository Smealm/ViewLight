using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ViewLight.Services;

/// <summary>
/// A Windows job object the launcher puts its server processes in, configured so that Windows stops
/// them when the launcher ends.
/// </summary>
/// <remarks>
/// <para>
/// ViewLight already stops the server it started while shutting down, but that path only runs on a
/// <i>graceful</i> close. Killed from Task Manager, or lost to a crash or a power cut, the Director
/// keeps running and keeps every port it bound - 12000, the realms, the patch server and the embedded
/// database - and the next Play then starts a second Director that cannot bind any of them.
/// </para>
/// <para>
/// A job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> fixes that at the source: the server is
/// tied to the launcher, so when the launcher process ends - however it ends, including a hard kill,
/// because the kernel closes every handle a dying process owns - the job's last handle closes and
/// Windows terminates everything in it. Child processes the Director spawns inherit the job, so they go
/// too.
/// </para>
/// <para>
/// Nothing else is limited: no memory cap, no CPU cap, no process-count cap. The only rule is
/// "stop when the launcher does".
/// </para>
/// </remarks>
public sealed class ProcessJob : IDisposable {

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;

    private readonly object _gate = new();
    private IntPtr _handle;

    /// <summary>Why an assignment failed, for the log line that reports it.</summary>
    public string? LastError { get; private set; }

    private ProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Creates the job, or returns <c>null</c> when the platform cannot provide one - a non-Windows
    /// build, or a machine where the calls are refused. Callers carry on without the tie rather than
    /// failing to start the server.
    /// </summary>
    /// <param name="error">Why no job was made, when that is worth saying.</param>
    public static ProcessJob? Create(out string? error) {
        error = null;

        if (!OperatingSystem.IsWindows()) {
            error = "Process jobs are a Windows feature.";
            return null;
        }

        try {
            // An unnamed job, so two launchers never share one: each ties only to its own servers.
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) {
                error = DescribeLastError();
                return null;
            }

            var job = new ProcessJob(handle);
            if (!job.ApplyKillOnCloseLimit()) {
                // Without the limit the job would hold processes without ever stopping them, which is a
                // false comfort, so it is closed again rather than used.
                error = job.LastError;
                job.Dispose();
                return null;
            }

            return job;
        }
        catch (Exception ex) {
            // DllNotFound / EntryPointNotFound on an unexpected platform.
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Creates the job, discarding the reason it could not be made.</summary>
    public static ProcessJob? Create() => Create(out _);

    private bool ApplyKillOnCloseLimit() {
        var information = new JobObjectExtendedLimitInformationData {
            BasicLimitInformation = new JobObjectBasicLimitInformation {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
        var buffer = Marshal.AllocHGlobal(size);

        try {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(
                    _handle, JobObjectExtendedLimitInformation, buffer, (uint)size)) {
                LastError = DescribeLastError();
                return false;
            }

            return true;
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Puts <paramref name="process"/> in the job. From here on, this launcher ending stops it.
    /// </summary>
    /// <returns><c>true</c> when the process is tied to the job.</returns>
    public bool Assign(Process process) {
        lock (_gate) {
            if (_handle == IntPtr.Zero) {
                LastError = "The job is already closed.";
                return false;
            }

            try {
                if (!AssignProcessToJobObject(_handle, process.Handle)) {
                    LastError = DescribeLastError();
                    return false;
                }

                return true;
            }
            catch (Exception ex) {
                // Reading the handle of a process that has already gone.
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Closes the job. Anything still in it is terminated by Windows as the last handle goes away, so
    /// this doubles as the guarantee that no server outlives the launcher.
    /// </summary>
    public void Dispose() {
        lock (_gate) {
            if (_handle == IntPtr.Zero) {
                return;
            }

            var handle = _handle;
            _handle = IntPtr.Zero;
            CloseHandle(handle);
        }

        GC.SuppressFinalize(this);
    }

    private static string DescribeLastError() {
        var code = Marshal.GetLastWin32Error();
        return code == 0 ? "Windows did not say why." : $"Windows error {code}.";
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation {
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
    private struct IoCounters {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
