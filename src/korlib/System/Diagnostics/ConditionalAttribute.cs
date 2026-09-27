// NeutrinoOS korlib - System.Diagnostics.ConditionalAttribute
//
// The C# compiler removes calls to methods marked [Conditional("SYM")]
// when SYM is not defined at the call site's compilation (standard .NET
// behavior). NeutrinoOS uses it for compile-time-gated kernel traces
// (src/kernel/Runtime/JitTrace.cs): without NEUTRINO_TRACE the trace
// call sites disappear from the image entirely.
//
// The namespace and type name must match the BCL exactly - the compiler
// recognizes conditional methods by the attribute's full name.

namespace System.Diagnostics;

/// <summary>
/// Indicates that the call to the marked method is compiled out unless
/// the given preprocessing symbol is defined.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ConditionalAttribute : Attribute
{
    /// <summary>The preprocessing symbol that enables the method's calls.</summary>
    public string ConditionString { get; }

    public ConditionalAttribute(string conditionString)
    {
        ConditionString = conditionString;
    }
}
