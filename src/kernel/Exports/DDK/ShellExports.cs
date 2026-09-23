// NeutrinoOS kernel - DDK shell bridge exports (Phase 6)
//
// Kernel_ShellExec: run one shell command line on behalf of a remote
// (SSH) session and capture its console output. Implemented with
// korlib's StringWriter + Console redirection so anything the command
// prints (stdout and stderr) is captured; the output buffer is
// NUL-terminated and the command's exit code is returned.
//
// Sessions run their commands through the same ShellExecutor as the
// local console, so built-ins, aliases, pipelines and path handling
// behave identically.

using System;
using System.IO;
using System.Runtime.InteropServices;
using ProtonOS.Shell;

namespace ProtonOS.Exports.DDK;

/// <summary>DDK shell bridge exports (see file header).</summary>
public static unsafe class ShellExports
{
    /// <summary>
    /// Execute <paramref name="line"/> (ASCII, explicit length); copy the
    /// captured output as a NUL-terminated string into
    /// <paramref name="outBuf"/>. Returns the command's exit code, or a
    /// negative value on bridge errors.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_ShellExec")]
    public static int ShellExec(byte* line, int lineLen, byte* outBuf, int outLen)
    {
        if (line == null || lineLen <= 0 || lineLen > 4096 || outBuf == null || outLen <= 0)
            return -1;

        var chars = new char[lineLen];
        for (int i = 0; i < lineLen; i++)
            chars[i] = (char)line[i];
        string command = new string(chars);

        var writer = new StringWriter();
        string output;
        int exitCode;
        try
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            exitCode = ShellExecutor.ExecuteLine(command);
        }
        catch (Exception)
        {
            exitCode = -2;
        }
        finally
        {
            Console.SetOut(null);
            Console.SetError(null);
            output = writer.ToString();
        }

        int n = output.Length;
        if (n > outLen - 1)
            n = outLen - 1;
        for (int i = 0; i < n; i++)
        {
            char c = output[i];
            outBuf[i] = c < 256 ? (byte)c : (byte)'?';
        }
        outBuf[n] = 0;
        return exitCode;
    }

    /// <summary>
    /// Console redirection for interactive sessions: route the local
    /// console's output to the caller's sink? Currently unsupported -
    /// returns 0. (Reserved for a future duplex bridge.)
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_ShellPing")]
    public static int ShellPing()
    {
        return 1;
    }
}
