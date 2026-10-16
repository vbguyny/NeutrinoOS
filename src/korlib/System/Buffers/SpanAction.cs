// NeutrinoOS korlib - System.Buffers.SpanAction<T, TArg> (M8.1 follow-up).
//
// Microsoft.Extensions.Primitives' StringValues.ToString() routes through
// string.Create<TState>(int, TState, SpanAction<char, TState>), so the
// delegate type must be resolvable by the device loader (TypeRef
// System.Buffers.SpanAction`2 scoped to System.Runtime maps to korlib).
// Declaring it in korlib makes it a real TypeDef in the shipped korlib IL,
// and the JIT's generic-instantiation machinery can then build the
// SpanAction<char, TState> MethodTable used by the newobj at the call site.
// The invocation itself is performed by the AOT StringHelpers.CreateString
// entry (see AotMethodRegistry), which reads the standard delegate layout.
namespace System.Buffers
{
    public delegate void SpanAction<T, in TArg>(Span<T> span, TArg arg);
}
