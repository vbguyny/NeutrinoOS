// NeutrinoOS TestSupport - Phase 4 shell runner helpers
//
// The kernel's shell `run` command executes JIT-compiled assemblies in
// kernel mode. Managed objects created by kernel (AOT) code are NOT
// visible to the GC's root scan (kernel frames report raw pointers, not
// GC references), so any managed object that must survive across
// allocations has to be created inside JIT-compiled code, where the
// JIT's own stack maps keep it alive.
//
// The kernel therefore passes raw UTF-16 buffers into these helpers and
// lets them materialize the managed String objects and the string[]
// argument array.

namespace TestSupport;

/// <summary>
/// JIT-side helpers for launching applications from the kernel shell.
/// </summary>
public static unsafe class ShellRunSupport
{
    // Keeps the current argument array (and, through its GC descriptor,
    // the strings inside) GC-rooted for the whole app run. JIT-assembly
    // static fields are GC roots; the array created below could otherwise
    // be missed by the Tier-0 JIT's GC info in some frames.
    private static string[]? _pinnedArgs;

    /// <summary>
    /// Materialize packed UTF-16 arguments and invoke an entry point with
    /// a managed <c>string[]</c> parameter.
    ///
    /// The array and its strings are created in this JIT-compiled frame,
    /// so they are GC-rooted for the entire call - including everything
    /// the invoked Main does.
    ///
    /// NOTE: strings are built with the explicit 3-arg char* constructor:
    /// the kernel's JIT bridges System.String's char* ctor to a fixed
    /// 3-parameter factory, so the 1-arg `new string(char*)` overload
    /// would pass garbage for startIndex/length.
    /// </summary>
    /// <param name="mainFn">JIT-compiled Main(string[]) entry point</param>
    /// <param name="packedArgs">UTF-16 arguments, packed back to back</param>
    /// <param name="argLengths">Length in chars of each argument</param>
    /// <param name="argc">Number of argument strings</param>
    /// <param name="returnsInt">1 for int Main(string[]), 0 for void Main(string[])</param>
    /// <returns>Main's exit code (0 for void Main)</returns>
    public static int InvokeMain(void* mainFn, char* packedArgs, int* argLengths, int argc, int returnsInt)
    {
        string[] args = new string[argc];
        _pinnedArgs = args;

        char* p = packedArgs;
        for (int i = 0; i < argc; i++)
        {
            int len = argLengths[i];
            args[i] = new string(p, 0, len);
            p += len;
        }

        if (returnsInt != 0)
        {
            var fn = (delegate*<string[], int>)mainFn;
            return fn(args);
        }
        else
        {
            var fn = (delegate*<string[], void>)mainFn;
            fn(args);
            return 0;
        }
    }
}
