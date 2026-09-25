// NeutrinoOS Phase 8 npkg test fixture: hello-utility payload.
// Tier-0-JIT-safe: simple strings and Console.WriteLine only (no Math,
// no Span, no string.Format, no Split, no number ToString, no char concat).

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("hello-utility ok");
        return 0;
    }
}
