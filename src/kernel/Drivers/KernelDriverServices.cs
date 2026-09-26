// ProtonOS Kernel - IDriverServices implementation for driver hosts.
//
// Drivers reach all kernel authority through this object: MMIO mapping, DMA,
// interrupts, device nodes and logging. In the current in-kernel host model
// these map directly onto kernel facilities (identity-mapped memory, the
// heap allocator, the IDT vector table).

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Memory;
using ProtonOS.Platform;
using ProtonOS.Arch;

namespace ProtonOS.Drivers;

/// <summary>Kernel implementation of the driver services ABI.</summary>
public sealed unsafe class KernelDriverServices : IDriverServices
{
    /// <summary>Shared instance handed to every driver host.</summary>
    public static readonly KernelDriverServices Instance = new KernelDriverServices();

    private const int MaxIrqs = 16;
    private const int MaxDmaBuffers = 64;

    // Interrupt dispatch: one thunk per IRQ line, forwarding to the driver
    // callback registered through the ABI.
    private static readonly DriverInterruptCallback[] _irqHandlers = new DriverInterruptCallback[MaxIrqs];
    // NOTE: byte[] rather than bool[] - bflat's corlib codegen fails on
    // bool arrays ("Code generation failed for method 'bool.ToString()'").
    private static readonly byte[] _irqInstalled = new byte[MaxIrqs];

    // DMA bookkeeping for alignment over-allocation: physical -> raw base.
    // NOTE: raw bases are stored as ulong - bflat's IL scanner rejects
    // arrays of pointer types (PointerType -> DefType cast failure).
    private static readonly ulong[] _dmaPhys = new ulong[MaxDmaBuffers];
    private static readonly ulong[] _dmaRaw = new ulong[MaxDmaBuffers];

    private KernelDriverServices()
    {
    }

    /// <summary>
    /// Map a physical MMIO region through the higher-half physical map
    /// window (cache disabled), like the DDK Kernel_MapMMIO export. Low
    /// BARs also remain identity-mapped, but 64-bit BARs assigned above
    /// 4 GiB (e.g. the qemu-xhci register window) only exist in this
    /// window.
    /// </summary>
    public ulong MapMmio(ulong physicalAddress, ulong size)
    {
        ulong virtAddr = VirtualMemory.PhysToVirt(physicalAddress);

        ulong physStart = physicalAddress & ~(VirtualMemory.LargePageSize - 1);
        ulong physEnd = (physicalAddress + size + VirtualMemory.LargePageSize - 1)
                      & ~(VirtualMemory.LargePageSize - 1);
        for (ulong phys = physStart; phys < physEnd; phys += VirtualMemory.LargePageSize)
        {
            ulong virt = VirtualMemory.PhysToVirt(phys);
            // Page might already be mapped - treat as success for MMIO.
            VirtualMemory.MapLargePage(virt, phys, PageFlags.KernelRW | PageFlags.CacheDisable);
        }
        return virtAddr;
    }

    /// <summary>Unmap a region returned by MapMmio (no-op on identity map).</summary>
    public void UnmapMmio(ulong virtualAddress, ulong size)
    {
        _ = virtualAddress;
        _ = size;
    }

    /// <summary>
    /// Allocate a zeroed physically-contiguous DMA buffer. On the identity
    /// map the returned physical address is also a valid virtual address.
    /// </summary>
    public ulong AllocateDma(ulong size, ulong alignment)
    {
        if (size == 0)
            return 0;
        if (alignment < 16)
            alignment = 16;

        // The heap allocator is 16-byte aligned; over-allocate when a larger
        // alignment is requested and remember the raw base for FreeDma.
        void* raw;
        ulong result;
        if (alignment <= 16)
        {
            raw = HeapAllocator.AllocZeroed(size);
            if (raw == null)
                return 0;
            result = (ulong)raw;
        }
        else
        {
            void* over = HeapAllocator.AllocZeroed(size + alignment);
            if (over == null)
                return 0;
            ulong aligned = ((ulong)over + alignment - 1) & ~(alignment - 1);
            raw = over;
            result = aligned;
        }

        // Record raw base for aligned buffers.
        if ((ulong)raw != result)
        {
            for (int i = 0; i < MaxDmaBuffers; i++)
            {
                if (_dmaPhys[i] == 0)
                {
                    _dmaPhys[i] = result;
                    _dmaRaw[i] = (ulong)raw;
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Free a buffer previously returned by AllocateDma.</summary>
    public void FreeDma(ulong physicalAddress, ulong size)
    {
        _ = size;
        if (physicalAddress == 0)
            return;

        for (int i = 0; i < MaxDmaBuffers; i++)
        {
            if (_dmaPhys[i] == physicalAddress)
            {
                HeapAllocator.Free((void*)_dmaRaw[i]);
                _dmaPhys[i] = 0;
                _dmaRaw[i] = 0;
                return;
            }
        }
        HeapAllocator.Free((void*)physicalAddress);
    }

    /// <summary>
    /// Register a driver interrupt handler for a legacy IRQ line (0-15).
    /// The handler runs in interrupt context and must be short.
    /// </summary>
    public bool RegisterInterrupt(int irq, DriverInterruptCallback handler)
    {
        if (irq < 0 || irq >= MaxIrqs || handler == null)
            return false;

        _irqHandlers[irq] = handler;

        if (_irqInstalled[irq] == 0)
        {
            // Legacy IRQ n is delivered on vector 32 + n (remapped PIC/IOAPIC).
            // Fully qualify: inside ProtonOS.Drivers, plain "Arch" binds to
            // the ProtonOS.Arch namespace, not the ProtonOS.Arch.Arch class.
            ProtonOS.Arch.Arch.RegisterHandler(32 + irq, &IrqThunk);
            _irqInstalled[irq] = 1;
        }
        return true;
    }

    /// <summary>Unregister a driver interrupt handler.</summary>
    public void UnregisterInterrupt(int irq)
    {
        if (irq < 0 || irq >= MaxIrqs)
            return;
        _irqHandlers[irq] = null;
    }

    /// <summary>Interrupt thunk: forwards to the registered driver callback.</summary>
    private static void IrqThunk(InterruptFrame* frame)
    {
        // The IOAPIC redirects legacy IRQs to vectors 32+n; the frame reports
        // the vector number.
        int vector = (int)frame->InterruptNumber;
        int irq = vector - 32;
        if (irq >= 0 && irq < MaxIrqs)
        {
            DriverInterruptCallback cb = _irqHandlers[irq];
            if (cb != null)
                cb(irq);
        }
#if !ARCH_ARM64
        // On ARM64 the GIC dispatcher has already EOI'd the interrupt
        // before the handler runs (see ExceptionVectors).
        APIC.SendEoi();
#endif
    }

    /// <summary>
    /// Create a device node under /dev. NOTE: wired to the console device
    /// registry once a driver needs it; currently returns the canonical path.
    /// </summary>
    public string CreateDeviceNode(string name, int major, int minor)
    {
        _ = major;
        _ = minor;
        if (name == null || name.Length == 0)
            return null;
        string path = "/dev/" + name;
        DebugConsole.Write("[drv] device node ");
        DebugConsole.WriteLine(path);
        return path;
    }

    /// <summary>Remove a device node created by CreateDeviceNode.</summary>
    public bool RemoveDeviceNode(string name)
    {
        _ = name;
        return false;
    }

    /// <summary>Write a log line to the kernel console.</summary>
    public void Log(DriverLogLevel level, string message)
    {
        DebugConsole.Write("[drv] ");
        if (level == DriverLogLevel.Error)
            DebugConsole.Write("[E] ");
        else if (level == DriverLogLevel.Warning)
            DebugConsole.Write("[W] ");
        else if (level == DriverLogLevel.Debug)
            DebugConsole.Write("[D] ");
        else
            DebugConsole.Write("[I] ");
        DebugConsole.WriteLine(message);
    }
}
