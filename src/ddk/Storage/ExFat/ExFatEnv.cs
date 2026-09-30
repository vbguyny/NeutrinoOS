// NeutrinoOS Phase 10 - exFAT execution environment shims.
//
// The exFAT core runs in two worlds: inside the kernel JIT (where
// buffers come from the page allocator and time from the RTC) and in
// the host-side test harness (plain .NET, where the kernel exports do
// not exist). ExFatScratch hides that difference behind one type and
// one switch; the kernel path is always the default.
//
// IMPORTANT: the Tier-0 JIT eagerly compiles the callees of every
// method it compiles, so kernel-reachable code must not *statically*
// reference host-only helpers (the JIT would chase the call and fail on
// BCL types like GCHandle that korlib does not provide). The managed
// (host-test) path therefore arrives through an abstract backend whose
// virtual call sites compile to plain vtable dispatch; the concrete
// GCHandle-backed backend is only ever referenced by the host harness.

using System;
using System.Runtime.CompilerServices;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>
/// Scratch allocation backend. The kernel never sets one (the page
/// allocator path is default); the host harness instals
/// <see cref="ManagedScratchBackend"/>.
/// </summary>
public abstract unsafe class ExFatScratchBackend
{
    /// <summary>Allocates a buffer and returns a token for free.</summary>
    public abstract byte* Alloc(int bytes, out nint token);

    /// <summary>Frees a buffer previously returned by <see cref="Alloc"/>.</summary>
    public abstract void Free(nint token);
}

/// <summary>
/// A scratch buffer provider. In the kernel it allocates physical pages
/// through the DDK; in host tests set <see cref="UseManaged"/> and
/// <see cref="Backend"/> first and it uses the managed backend instead.
/// </summary>
public sealed unsafe class ExFatScratch
{
    /// <summary>When true, use the managed backend (host tests).</summary>
    public static bool UseManaged;

    /// <summary>Host-test allocation backend (null in the kernel).</summary>
    public static ExFatScratchBackend? Backend;

    private byte* _ptr;
    private ulong _phys;
    private bool _kernel;
    private nint _pinHandle;

    /// <summary>Allocated capacity in bytes.</summary>
    public int Capacity { get; private set; }

    /// <summary>
    /// Returns a buffer of at least <paramref name="bytes"/> bytes
    /// (allocated once; a larger request re-allocates).
    /// </summary>
    /// <param name="bytes">Required size.</param>
    public byte* Get(int bytes)
    {
        if (_ptr != null && Capacity >= bytes)
            return _ptr;
        Release();

        if (UseManaged)
        {
            nint token;
            _ptr = Backend!.Alloc(bytes, out token);
            _pinHandle = token;
            _kernel = false;
            Capacity = _ptr != null ? bytes : 0;
            return _ptr;
        }

        ulong pages = ((ulong)bytes + 4095) / 4096;
        ulong phys = Kernel.Memory.AllocatePages(pages);
        if (phys == 0)
            return null;
        _phys = phys;
        _ptr = (byte*)Kernel.Memory.PhysToVirt(phys);
        Capacity = bytes;
        _kernel = true;
        return _ptr;
    }

    /// <summary>Frees the buffer (page-free or backend release).</summary>
    public void Release()
    {
        if (_ptr == null)
            return;
        if (_kernel)
        {
            ulong pages = ((ulong)Capacity + 4095) / 4096;
            Kernel.Memory.FreePages(_phys, pages);
        }
        else if (_pinHandle != 0)
        {
            Backend!.Free(_pinHandle);
            _pinHandle = 0;
        }
        _ptr = null;
        _phys = 0;
        Capacity = 0;
    }
}

/// <summary>
/// Host-test backend: pins a managed array with GCHandle. Never
/// referenced by kernel-compiled code (see the file header), so the
/// Tier-0 JIT never resolves the GCHandle types.
/// </summary>
public sealed class ManagedScratchBackend : ExFatScratchBackend
{
    /// <summary>Allocates and pins a byte array; the token is its GCHandle.</summary>
    public override unsafe byte* Alloc(int bytes, out nint token)
    {
        var arr = new byte[bytes];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(
            arr, System.Runtime.InteropServices.GCHandleType.Pinned);
        token = System.Runtime.InteropServices.GCHandle.ToIntPtr(pin);
        return (byte*)pin.AddrOfPinnedObject().ToPointer();
    }

    /// <summary>Unpins a managed array.</summary>
    public override unsafe void Free(nint token)
    {
        var pin = System.Runtime.InteropServices.GCHandle.FromIntPtr(token);
        pin.Free();
    }
}

/// <summary>Time source shim: kernel RTC by default, fixed value for tests.</summary>
public static class ExFatClock
{
    /// <summary>When nonzero, "now" returns this epoch value (host tests).</summary>
    public static long OverrideNowEpoch;
}
