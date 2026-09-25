// ProtonOS Architecture Abstraction - Compile-Time Dispatch
// Global using aliases for architecture selection.
// The correct architecture types are selected at compile time via preprocessor defines.

// Note: These global usings provide convenient aliases so that kernel code can write:
//   CurrentCpu.DisableInterrupts();
//   CurrentEmitter.EmitPrologue(ref code, 16);
// Instead of:
//   #if ARCH_X64
//   ProtonOS.Arch.CPU.DisableInterrupts();
//   #endif

#if ARCH_X64

global using CurrentArch = ProtonOS.Arch.Arch;
global using CurrentCpu = ProtonOS.Arch.CPU;
global using CurrentVMem = ProtonOS.Arch.VirtualMemory;
global using CurrentEmitter = ProtonOS.Runtime.JIT.X64Emitter;

#elif ARCH_ARM64

global using CurrentArch = ProtonOS.Arch.Arch;
global using CurrentCpu = ProtonOS.Arch.CPU;
global using CurrentVMem = ProtonOS.Arch.VirtualMemory;
// No ARM64 code emitter yet: JIT compilation is x64-only for now, so the
// alias points at the x64 emitter (the type only matters where JIT code
// paths are compiled; the ARM64 kernel does not execute them).
global using CurrentEmitter = ProtonOS.Runtime.JIT.X64Emitter;

#else

// Default: Assume x64 if no architecture specified
// This allows existing code to continue working without -d ARCH_X64

#endif

namespace ProtonOS.Arch;

/// <summary>
/// Architecture detection utilities.
/// Provides compile-time constants for architecture selection.
/// </summary>
public static class ArchInfo
{
#if ARCH_X64
    public const string Name = "x64";
    public const bool IsX64 = true;
    public const bool IsArm64 = false;
    public const int PointerSize = 8;
    public const int RegisterCount = 16;
    public const int FloatRegisterCount = 16;
#elif ARCH_ARM64
    public const string Name = "arm64";
    public const bool IsX64 = false;
    public const bool IsArm64 = true;
    public const int PointerSize = 8;
    public const int RegisterCount = 31;
    public const int FloatRegisterCount = 32;
#else
    // Default to x64 for backwards compatibility
    public const string Name = "x64";
    public const bool IsX64 = true;
    public const bool IsArm64 = false;
    public const int PointerSize = 8;
    public const int RegisterCount = 16;
    public const int FloatRegisterCount = 16;
#endif
}
