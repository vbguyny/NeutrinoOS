// NeutrinoOS korlib - System.Threading.Volatile (M8: NuGet package support)
//
// Managed threads on NeutrinoOS run cooperatively and every managed
// access is a plain memory access from the kernel's point of view, so
// Volatile.Read/Write forward directly to the underlying slot. The
// member shapes match the BCL so packages (M8:
// Microsoft.Extensions.Primitives' ChangeToken machinery) resolve and
// run unchanged.

namespace System.Threading;

/// <summary>Volatile read/write helpers (see file header).</summary>
public static class Volatile
{
    /// <summary>Reads a reference-typed location.</summary>
    public static T Read<T>(ref T location) where T : class => location;

    /// <summary>Writes a reference-typed location.</summary>
    public static void Write<T>(ref T location, T value) where T : class => location = value;

    /// <summary>Reads an Int32 location.</summary>
    public static int Read(ref int location) => location;

    /// <summary>Writes an Int32 location.</summary>
    public static void Write(ref int location, int value) => location = value;

    /// <summary>Reads an Int64 location.</summary>
    public static long Read(ref long location) => location;

    /// <summary>Writes an Int64 location.</summary>
    public static void Write(ref long location, long value) => location = value;

    /// <summary>Reads a Boolean location.</summary>
    public static bool Read(ref bool location) => location;

    /// <summary>Writes a Boolean location.</summary>
    public static void Write(ref bool location, bool value) => location = value;
}
