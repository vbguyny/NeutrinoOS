// NeutrinoOS Phase 8 npkg test fixture: hello-driver payload.
// A plain driver class (no DDK references yet - the on-device driver
// framework binds it by class name once driver hosting lands).
// Tier-0-JIT-safe: simple strings and Console.WriteLine only.

using System;

public sealed class HelloLedDriver
{
    public bool Initialize()
    {
        return true;
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("hello-driver ok");
        return 0;
    }
}
