// NeutrinoOS runtime diagnostics switches
// Central switches for optional verbose tracing in the kernel runtime.
// Verbose traces emit megabytes of serial output, and every serial byte
// is expensive on hypervisors that trap port I/O (e.g. VirtualBox running
// under NEM/WHPX). They are therefore off by default - boot the
// "verbose-jit" marker file to re-enable them when debugging.

namespace ProtonOS.Runtime;

public static class JitDiag
{
    /// <summary>
    /// Enable verbose JIT emission traces (per call-site, per local, and
    /// per type resolution). Selected by the "verbose-jit" boot marker.
    /// </summary>
    public static bool VerboseJit;

    /// <summary>
    /// Number of methods compiled by the Tier-0 JIT so far. Printed as a
    /// running count in the "[JIT] Compile #n ..." progress lines.
    /// </summary>
    public static int CompiledMethods;
}
