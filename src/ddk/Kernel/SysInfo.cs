// ProtonOS DDK - System information kernel wrappers (Phase 5)
//
// DllImport wrappers for the NeutrinoOS system-information exports
// (Kernel_* names registered by KernelExportInit). Used by the Phase 5
// utility suite (env, date, uname, ps, kill) to read kernel state from
// JIT-compiled code.

using System;
using System.Runtime.InteropServices;

namespace ProtonOS.DDK.Kernel;

/// <summary>DDK wrappers for the NeutrinoOS system-information exports.</summary>
public static unsafe class SysInfo
{
    /// <summary>Number of environment variables.</summary>
    [DllImport("*", EntryPoint = "Kernel_GetEnvironmentVariableCount")]
    public static extern int GetEnvironmentVariableCount();

    /// <summary>
    /// Name and value of environment variable <paramref name="index"/>
    /// (raw form; UTF-16 buffers; returns the value length with the name
    /// length written to <paramref name="nameLenOut"/>, or -1).
    /// </summary>
    [DllImport("*", EntryPoint = "Kernel_GetEnvironmentVariableAt")]
    public static extern int GetEnvironmentVariableAt(int index, char* nameBuf, int nameCapacity,
        int* nameLenOut, char* valueBuf, int valueCapacity);

    /// <summary>Wall-clock time from the CMOS RTC (zeros when no RTC).</summary>
    [DllImport("*", EntryPoint = "Kernel_GetWallClock")]
    public static extern void GetWallClock(out int year, out int month, out int day,
        out int hour, out int minute, out int second);

    /// <summary>NeutrinoOS version string (raw form; returns its length).</summary>
    [DllImport("*", EntryPoint = "Kernel_GetNeutrinoVersion")]
    public static extern int GetNeutrinoVersion(char* buffer, int capacity);

    /// <summary>Number of background shell jobs.</summary>
    [DllImport("*", EntryPoint = "Kernel_GetShellJobCount")]
    public static extern int GetShellJobCount();

    /// <summary>Shell job info (raw form; returns command length or -1).</summary>
    [DllImport("*", EntryPoint = "Kernel_GetShellJobAt")]
    public static extern int GetShellJobAt(int index, int* id, int* pid, int* state, int* exitCode,
        char* commandBuf, int commandCapacity);

    /// <summary>Cooperative kill of a shell job by id/pid (0 ok, -1 unknown).</summary>
    [DllImport("*", EntryPoint = "Kernel_KillShellJob")]
    public static extern int KillShellJob(int idOrPid);

    /// <summary>Number of kernel threads.</summary>
    [DllImport("*", EntryPoint = "Kernel_GetThreadCount")]
    public static extern int GetThreadCount();

    /// <summary>Kernel thread info (raw form; 0 ok, -1 bad index).</summary>
    [DllImport("*", EntryPoint = "Kernel_GetThreadInfoAt")]
    public static extern int GetThreadInfoAt(int index, uint* threadId, int* state, ulong* stackSize);

    /// <summary>Boot-volume stats (raw; label length or negative error).</summary>
    [DllImport("*", EntryPoint = "Kernel_GetBootVolumeStats")]
    public static extern int GetBootVolumeStats(char* labelBuf, int labelCapacity,
        ulong* totalBytes, ulong* freeBytes);

    /// <summary>Managed helper: volume label plus total/free bytes.</summary>
    public static bool TryGetBootVolumeStats(out string label, out ulong totalBytes, out ulong freeBytes)
    {
        const int Cap = 64;
        char* labelBuf = stackalloc char[Cap];
        ulong total;
        ulong free;
        int len = GetBootVolumeStats(labelBuf, Cap, &total, &free);
        if (len < 0)
        {
            label = "";
            totalBytes = 0;
            freeBytes = 0;
            return false;
        }
        label = new string(labelBuf, 0, len);
        totalBytes = total;
        freeBytes = free;
        return true;
    }

    /// <summary>Managed helper: shell job info #index (ps utility).</summary>
    public static bool TryGetShellJobInfo(int index, out int pid, out int jobId,
        out int state, out int exitCode, out string command)
    {
        const int Cap = 128;
        char* cmdBuf = stackalloc char[Cap];
        int id;
        int pidOut;
        int st;
        int exit;
        int len = GetShellJobAt(index, &id, &pidOut, &st, &exit, cmdBuf, Cap);
        if (len < 0)
        {
            pid = 0;
            jobId = 0;
            state = 0;
            exitCode = 0;
            command = "";
            return false;
        }
        pid = pidOut;
        jobId = id;
        state = st;
        exitCode = exit;
        command = new string(cmdBuf, 0, len);
        return true;
    }

    /// <summary>Managed helper: kernel thread info #index.</summary>
    public static bool TryGetThreadInfo(int index, out uint threadId, out int state, out ulong stackSize)
    {
        uint id;
        int st;
        ulong sz;
        if (GetThreadInfoAt(index, &id, &st, &sz) != 0)
        {
            threadId = 0;
            state = 0;
            stackSize = 0;
            return false;
        }
        threadId = id;
        state = st;
        stackSize = sz;
        return true;
    }

    /// <summary>Managed helper: reads environment variable <paramref name="index"/> as strings.</summary>
    public static bool TryGetEnvironmentVariable(int index, out string name, out string value)
    {
        const int Cap = 256;
        char* nameBuf = stackalloc char[Cap];
        char* valueBuf = stackalloc char[Cap];
        int nameLen;
        int valueLen = GetEnvironmentVariableAt(index, nameBuf, Cap, &nameLen, valueBuf, Cap);
        if (valueLen < 0)
        {
            name = "";
            value = "";
            return false;
        }

        name = new string(nameBuf, 0, nameLen);
        value = new string(valueBuf, 0, valueLen);
        return true;
    }

    /// <summary>Managed helper: the NeutrinoOS version string.</summary>
    public static string GetVersionString()
    {
        const int Cap = 128;
        char* buffer = stackalloc char[Cap];
        int len = GetNeutrinoVersion(buffer, Cap);
        if (len <= 0)
            return "NeutrinoOS";
        return new string(buffer, 0, len);
    }

    /// <summary>Managed helper: wall-clock time; false when no RTC is present.</summary>
    public static bool TryGetWallClock(out int year, out int month, out int day,
        out int hour, out int minute, out int second)
    {
        GetWallClock(out year, out month, out day, out hour, out minute, out second);
        return year > 0;
    }
}
