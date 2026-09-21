// NeutrinoOS Phase 4 test app 7 - class library (multi-assembly test)
//
// Deployed to /lib/MathShared.dll; loaded on demand by the kernel
// AssemblyLoader when the phase4multi app resolves its AssemblyRef.

namespace MathShared;

/// <summary>Simple math helpers used to verify cross-assembly loading.</summary>
public static class MathSharedMath
{
    /// <summary>Adds two integers.</summary>
    public static int Add(int a, int b) => a + b;

    /// <summary>Returns a marker string so the caller can verify which assembly responded.</summary>
    public static string Describe() => "MathShared v1";

    /// <summary>Multiplies two integers (second call site, exercises the cached resolution).</summary>
    public static int Multiply(int a, int b) => a * b;
}
