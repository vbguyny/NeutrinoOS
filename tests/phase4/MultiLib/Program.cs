// NeutrinoOS Phase 4 test app 7 - Multi-assembly main program
//
// Calls into MathShared.dll, which is NOT preloaded at boot: the kernel
// AssemblyLoader resolves the AssemblyRef on demand from /lib/MathShared.dll
// through the Phase 4 file bridge. Exit code 0 when both calls succeed.

using System;
using MathShared;

namespace Phase4.MultiLib;

public static class Program
{
    public static int Main()
    {
        int sum = MathSharedMath.Add(20, 22);
        Console.WriteLine("[multi] MathShared.Add(20, 22) = " + sum.ToString());

        int product = MathSharedMath.Multiply(6, 7);
        Console.WriteLine("[multi] MathShared.Multiply(6, 7) = " + product.ToString());

        string marker = MathSharedMath.Describe();
        Console.WriteLine("[multi] library says: " + marker);

        bool ok = sum == 42 && product == 42 && marker == "MathShared v1";
        Console.WriteLine(ok ? "[multi] PASS" : "[multi] FAIL");
        return ok ? 0 : 1;
    }
}
