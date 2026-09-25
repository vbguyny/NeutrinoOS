// NeutrinoOS driver framework - kernel services exposed to driver hosts.
//
// Drivers receive an IDriverServices instance from the host and use it for
// everything that requires kernel authority: mapping hardware resources,
// allocating DMA memory, registering interrupt handlers, creating device
// nodes and logging. Drivers must not use kernel internals directly.

namespace NeutrinoOS.Drivers
{
    /// <summary>Log severity for IDriverServices.Log.</summary>
    public enum DriverLogLevel : int
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    /// <summary>Interrupt callback invoked for a registered IRQ line.</summary>
    public delegate void DriverInterruptCallback(int irq);

    /// <summary>
    /// Kernel services available to a driver host. One instance is shared by
    /// all drivers in the host; resource operations are scoped to the
    /// devices those drivers own.
    /// </summary>
    public interface IDriverServices
    {
        /// <summary>
        /// Map a physical MMIO region into the driver's address space and
        /// return the base virtual address. Returns 0 on failure.
        /// </summary>
        ulong MapMmio(ulong physicalAddress, ulong size);

        /// <summary>Unmap a region previously returned by MapMmio.</summary>
        void UnmapMmio(ulong virtualAddress, ulong size);

        /// <summary>
        /// Allocate a physically-contiguous DMA buffer. Returns the physical
        /// address of the buffer (0 on failure). The buffer is zeroed.
        /// </summary>
        ulong AllocateDma(ulong size, ulong alignment);

        /// <summary>Free a buffer previously returned by AllocateDma.</summary>
        void FreeDma(ulong physicalAddress, ulong size);

        /// <summary>
        /// Register a handler for an IRQ line. The handler runs in interrupt
        /// context: it must be short and must not allocate or block.
        /// </summary>
        bool RegisterInterrupt(int irq, DriverInterruptCallback handler);

        /// <summary>Unregister a previously registered interrupt handler.</summary>
        void UnregisterInterrupt(int irq);

        /// <summary>
        /// Create a device node under /dev (e.g. "virtio-blk0", major=8).
        /// Returns the full node path, or null on failure.
        /// </summary>
        string CreateDeviceNode(string name, int major, int minor);

        /// <summary>Remove a device node created by CreateDeviceNode.</summary>
        bool RemoveDeviceNode(string name);

        /// <summary>Write a log line to the kernel console.</summary>
        void Log(DriverLogLevel level, string message);
    }
}
