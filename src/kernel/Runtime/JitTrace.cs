// NeutrinoOS kernel - compile-time-gated runtime traces
//
// Every method here is [Conditional("NEUTRINO_TRACE")]: unless the symbol
// is defined (the default build) the C# compiler removes each call site
// entirely - arguments included - so traces cost nothing and never print.
// Build with the trace flag to compile them in:
//
//   make image TRACE=1     (WSL / direct make)
//   ./build.sh --trace     (Windows wrapper)
//
// Trace builds also default JitDiag.VerboseJit to true, so the runtime
// verbose traces are active without the "verbose-jit" boot marker.
//
// Use JitTrace (not DebugConsole) for troubleshooting lines that must not
// appear in normal boots. Errors and boot status stay on DebugConsole so
// they always print.

using System.Diagnostics;
using ProtonOS.Platform;

namespace ProtonOS.Runtime;

/// <summary>Compile-time-gated runtime traces (see file header).</summary>
public static class JitTrace
{
    [Conditional("NEUTRINO_TRACE")]
    public static void Write(string text) => DebugConsole.Write(text);

    [Conditional("NEUTRINO_TRACE")]
    public static void WriteLine(string text) => DebugConsole.WriteLine(text);

    [Conditional("NEUTRINO_TRACE")]
    public static void WriteLine() => DebugConsole.WriteLine();

    [Conditional("NEUTRINO_TRACE")]
    public static void WriteHex(ulong value) => DebugConsole.WriteHex(value);

    [Conditional("NEUTRINO_TRACE")]
    public static void WriteDecimal(uint value) => DebugConsole.WriteDecimal(value);

    [Conditional("NEUTRINO_TRACE")]
    public static void WriteByte(byte value) => DebugConsole.WriteByte(value);

    [Conditional("NEUTRINO_TRACE")]
    public static void WriteChar(char value) => DebugConsole.WriteChar(value);
}
