// NeutrinoOS kernel - Phase 5 system information exports
//
// Kernel-side implementations exposed to JIT-compiled utilities through
// the named export registry (KernelExportInit): the Phase 5 utility
// suite (env, date, uname, ps, kill, ...) reads kernel state through
// these, via the ProtonOS.DDK wrappers (src/ddk/Kernel/SysInfo.cs).
//
// Call convention: raw pointers only (char* UTF-16, int*, uint*); no
// managed references cross the boundary.

using System;
using System.Runtime.InteropServices;
using ProtonOS.Threading;
using ProtonOS.X64;

namespace ProtonOS.Exports.DDK;

/// <summary>System information exports (see file header).</summary>
public static unsafe class SystemInfoExports
{
    /// <summary>
    /// Returns the number of environment variables (0 on failure).
    /// Backed by korlib's System.Environment table, which the shell and
    /// all applications share.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetEnvironmentVariableCount()
    {
        string[] names = Environment.GetEnvironmentVariableNames();
        return names.Length;
    }

    /// <summary>
    /// Copies the name and value of environment variable
    /// <paramref name="index"/> into the caller's buffers (UTF-16, not
    /// NUL-terminated; truncated to the capacities). The name length is
    /// written to <paramref name="nameLenOut"/>; the return value is the
    /// value length, or -1 for a bad index.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetEnvironmentVariableAt(int index, char* nameBuf, int nameCapacity,
        int* nameLenOut, char* valueBuf, int valueCapacity)
    {
        string[] names = Environment.GetEnvironmentVariableNames();
        if (index < 0 || index >= names.Length)
            return -1;

        string name = names[index];
        string value = Environment.GetEnvironmentVariable(name) ?? "";

        int nameLen = name.Length;
        if (nameLen > nameCapacity)
            nameLen = nameCapacity;
        for (int i = 0; i < nameLen; i++)
            nameBuf[i] = name[i];
        *nameLenOut = nameLen;

        int valueLen = value.Length;
        if (valueLen > valueCapacity)
            valueLen = valueCapacity;
        for (int i = 0; i < valueLen; i++)
            valueBuf[i] = value[i];

        return valueLen;
    }

    /// <summary>
    /// Writes the current wall-clock time from the CMOS RTC. Values are
    /// zero when the RTC is not initialized.
    /// </summary>
    [UnmanagedCallersOnly]
    public static void GetWallClock(int* year, int* month, int* day, int* hour, int* minute, int* second)
    {
        if (!RTC.IsInitialized)
        {
            *year = 0; *month = 0; *day = 0;
            *hour = 0; *minute = 0; *second = 0;
            return;
        }

        RTC.GetSystemTime(out int y, out int mo, out int d, out int h, out int mi, out int s, out int ms);
        *year = y; *month = mo; *day = d;
        *hour = h; *minute = mi; *second = s;
    }

    /// <summary>
    /// NeutrinoOS release version (Phase 7 semantic version).
    /// </summary>
    public const string ReleaseVersion = "1.0.0";

    /// <summary>
    /// The full version string ("NeutrinoOS &lt;version&gt; &lt;arch&gt;").
    /// Must be const: bflat's TypePreinit pass rejects static string
    /// field initializers in the kernel assembly.
    /// </summary>
    public const string VersionString = "NeutrinoOS 1.0.0 x86_64";

    /// <summary>
    /// Copies the NeutrinoOS version string ("NeutrinoOS &lt;version&gt;
    /// &lt;arch&gt;"; UTF-16, not NUL-terminated) into the caller's
    /// buffer and returns its length (truncated to the capacity).
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetNeutrinoVersion(char* buffer, int capacity)
    {
        string version = VersionString;
        int len = version.Length;
        if (len > capacity)
            len = capacity;
        for (int i = 0; i < len; i++)
            buffer[i] = version[i];
        return len;
    }

    /// <summary>
    /// Returns the number of background shell jobs (queued, running or
    /// finished). Used by the ps/kill utilities.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetShellJobCount()
    {
        return Shell.JobManager.Count;
    }

    /// <summary>
    /// Writes shell job info for job <paramref name="index"/> (0-based):
    /// job id, pid, state (0 queued, 1 running, 2 done, 3 killed), exit
    /// code and the command line (UTF-16, truncated). Returns the command
    /// length or -1 for a bad index.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetShellJobAt(int index, int* id, int* pid, int* state, int* exitCode,
        char* commandBuf, int commandCapacity)
    {
        string command = Shell.JobManager.GetJobInfo(index, out int jobId, out int jobPid,
            out int jobState, out int jobExit);
        if (command == null)
            return -1;

        *id = jobId;
        *pid = jobPid;
        *state = jobState;
        *exitCode = jobExit;

        int len = command.Length;
        if (len > commandCapacity)
            len = commandCapacity;
        for (int i = 0; i < len; i++)
            commandBuf[i] = command[i];
        return len;
    }

    /// <summary>
    /// Requests cancellation of a shell background job by job id or pid
    /// (cooperative; see docs/PHASE5-SHELL.md). Returns 0 on success,
    /// -1 for no such job.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int KillShellJob(int idOrPid)
    {
        return Shell.JobManager.KillForExport(idOrPid);
    }

    /// <summary>
    /// Phase 5: boot (FAT32) volume stats for the df utility. Writes the
    /// volume label (UTF-16) and the total/free byte counts; returns the
    /// label length or a negative error code.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetBootVolumeStats(char* labelBuf, int labelCapacity,
        ulong* totalBytes, ulong* freeBytes)
    {
        return Platform.FileExports.KernelBootVolumeStats(labelBuf, labelCapacity, totalBytes, freeBytes);
    }

    /// <summary>
    /// Writes kernel thread info for thread <paramref name="index"/>
    /// (0-based, walking the all-threads list): thread id, state
    /// (0..5 per ProtonOS.Threading.ThreadState) and its kernel stack
    /// size. Returns 0 on success, -1 for a bad index.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetThreadInfoAt(int index, uint* threadId, int* state, ulong* stackSize)
    {
        int i = 0;
        for (Thread* thread = Scheduler.AllThreadsHead; thread != null; thread = thread->NextAll)
        {
            if (i == index)
            {
                *threadId = thread->Id;
                *state = (int)thread->State;
                *stackSize = (ulong)thread->StackSize;
                return 0;
            }
            i++;
        }
        return -1;
    }
}
