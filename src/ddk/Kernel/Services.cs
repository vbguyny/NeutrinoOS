// ProtonOS DDK - Kernel service + shell bridge wrappers (Phase 6)
// DllImport wrappers for the cooperative background-service registry
// and the remote-session shell bridge.

using System;
using System.Runtime.InteropServices;

namespace ProtonOS.DDK.Kernel;

/// <summary>DDK wrappers for background services (see file header).</summary>
public static unsafe class Services
{
    [DllImport("*", EntryPoint = "Kernel_ServiceStart")]
    private static extern int ServiceStartRaw(byte* name, int length);

    [DllImport("*", EntryPoint = "Kernel_ServiceStop")]
    private static extern int ServiceStopRaw(byte* name, int length);

    private static int AsciiCall(string name, bool start)
    {
        if (name == null || name.Length == 0 || name.Length > 64)
            return -1;
        var buf = new byte[name.Length];
        for (int i = 0; i < name.Length; i++)
            buf[i] = (byte)name[i];
        fixed (byte* p = buf)
        {
            return start ? ServiceStartRaw(p, buf.Length) : ServiceStopRaw(p, buf.Length);
        }
    }

    /// <summary>Start a DDK background service by name; 0 = ok.</summary>
    public static int Start(string name) => AsciiCall(name, true);

    /// <summary>Stop a running service by name; 0 = ok.</summary>
    public static int Stop(string name) => AsciiCall(name, false);
}

/// <summary>DDK wrapper for the kernel shell bridge (remote sessions).</summary>
public static unsafe class ShellBridge
{
    [DllImport("*", EntryPoint = "Kernel_ShellExec")]
    private static extern int ShellExecRaw(byte* line, int lineLen, byte* outBuf, int outLen);

    /// <summary>
    /// Execute one shell command line and return its captured output.
    /// <paramref name="exitCode"/> receives the command's exit code.
    /// </summary>
    public static string Exec(string line, out int exitCode)
    {
        exitCode = -1;
        if (line == null)
            return "";

        var lineBytes = new byte[line.Length];
        for (int i = 0; i < line.Length; i++)
            lineBytes[i] = (byte)line[i];

        // Console output for one command rarely exceeds this; longer
        // output is truncated (documented).
        const int OutCap = 32768;
        var outBytes = new byte[OutCap];

        fixed (byte* lp = lineBytes)
        fixed (byte* op = outBytes)
        {
            exitCode = ShellExecRaw(lp, lineBytes.Length, op, OutCap);
        }

        int n = 0;
        while (n < OutCap && outBytes[n] != 0)
            n++;

        var chars = new char[n];
        for (int i = 0; i < n; i++)
            chars[i] = (char)outBytes[i];
        return new string(chars);
    }
}
