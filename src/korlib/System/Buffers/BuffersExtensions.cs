// NeutrinoOS korlib - BuffersExtensions (Kestrel port milestone M2)
//
// The BCL exposes ReadOnlySequence<T>.ToArray as an extension method on
// System.Buffers.BuffersExtensions; code compiled against the BCL (the DDK
// and JIT tests) binds to BuffersExtensions.ToArray<T>, so korlib must
// provide the type for on-device MemberRef resolution. The signature must
// match the BCL exactly - including the `in` (byref) parameter, which is
// part of the MemberRef signature the compiler emits.

namespace System.Buffers
{
    public static class BuffersExtensions
    {
        public static T[] ToArray<T>(this in ReadOnlySequence<T> sequence) => sequence.ToArray();
    }
}
