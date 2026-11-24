using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StemCode.VS.Services
{
    /// <summary>
    /// Keeps the StemCode CLI and everything it spawns tied to this process. An npm-installed
    /// `stemcode` is a cmd.exe shim that starts node, which starts StemCode.CLI.exe, so
    /// <see cref="Process.Kill()"/> on the shim alone orphans the rest. The Windows job object
    /// kills the whole tree when it is closed, including when Visual Studio exits or crashes.
    /// </summary>
    internal sealed class ProcessTree : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private IntPtr _job;

        private ProcessTree(IntPtr job) => _job = job;

        /// <summary>Puts <paramref name="process"/> (and its future children) in a kill-on-close job.</summary>
        public static ProcessTree? TryAttach(Process process, LogService log)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                log.Warn("Couldn't create a job object for the StemCode CLI: " + new Win32Exception().Message);
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length) ||
                    !AssignProcessToJobObject(job, process.Handle))
                {
                    log.Warn("Couldn't tie the StemCode CLI to Visual Studio's lifetime: " + new Win32Exception().Message);
                    CloseHandle(job);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new ProcessTree(job);
        }

        /// <summary>Terminates every process in the tree.</summary>
        public void Kill()
        {
            if (_job != IntPtr.Zero) TerminateJobObject(_job, 1);
        }

        public void Dispose()
        {
            IntPtr job = _job;
            _job = IntPtr.Zero;
            if (job != IntPtr.Zero) CloseHandle(job); // kill-on-close ends anything still running.
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
