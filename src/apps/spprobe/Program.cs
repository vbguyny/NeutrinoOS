// Span/MemoryExtensions JIT regression probe (M8 NuGet milestone).
//
// Devices run it via:  run /apps/webapp/spprobe.dll
//
// Guards the kernel JIT paths that Microsoft.Extensions.Primitives exercises
// on-device (StringSegment.StartsWith/Equals/IndexOf go through
// MemoryExtensions):
//   1. ReadOnlySpan<char> values as one/both 16-byte struct ARGUMENTS
//      (CompileCall previously popped arg1's first qword - the span's data
//      pointer - into RDX when BOTH arg0 and arg1 were large structs).
//   2. $iN element-strided get_Item selection for memberrefs whose element
//      type is a generic parameter (MemoryExtensions.IndexOf<T> compiled for
//      T=char used the byte-strided entry; IndexOf('l') in "hello" returned 4).
// Expected output: indexof=2, starts=T, equals=T, cmp=0, and "spprobe done".
using System;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("spprobe start");

        string text = "hello world";

        // 1) AsSpan(string, int, int) -> span over the string chars
        ReadOnlySpan<char> span = MemoryExtensions.AsSpan(text, 0, 5);
        Console.WriteLine("span.len=" + span.Length.ToString());
        Console.WriteLine("span0=" + ((int)span[0]).ToString());
        Console.WriteLine("span4=" + ((int)span[4]).ToString());

        // 2) AsSpan(string) -> span over the whole literal
        ReadOnlySpan<char> v = MemoryExtensions.AsSpan("hello");
        Console.WriteLine("v.len=" + v.Length.ToString());
        Console.WriteLine("v0=" + ((int)v[0]).ToString());
        Console.WriteLine("v4=" + ((int)v[4]).ToString());

        // 3) generic IndexOf<char> (ONE span arg + char) first - isolates
        //    single-span argument marshaling.
        Console.WriteLine("calling indexof");
        int idx = MemoryExtensions.IndexOf<char>(span, 'l');
        Console.WriteLine("indexof=" + idx.ToString());

        // 4) span-vs-span ops (BCL-visible overloads; device resolves them to
        //    korlib's MemoryExtensions implementations)
        Console.WriteLine("calling starts");
        bool st = MemoryExtensions.StartsWith(span, v, StringComparison.Ordinal);
        Console.WriteLine("starts=" + (st ? "T" : "F"));

        Console.WriteLine("calling equals");
        bool eq = MemoryExtensions.Equals(span, v, StringComparison.Ordinal);
        Console.WriteLine("equals=" + (eq ? "T" : "F"));

        Console.WriteLine("calling compareto");
        int cmp = MemoryExtensions.CompareTo(span, v, StringComparison.Ordinal);
        Console.WriteLine("cmp=" + cmp.ToString());

        Console.WriteLine("spprobe done");
    }
}
