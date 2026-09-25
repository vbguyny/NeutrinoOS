// NeutrinoOS driver framework - driver host interfaces.
//
// A driver host is a process that hosts one or more drivers. The kernel's
// driver manager loads drivers into a host, discovers them via
// IDriverHost.Drivers, and communicates device and interrupt events through
// IDriverHostServices. In the current implementation drivers run in managed
// host contexts inside the kernel world; the interface is designed so they
// can later move to isolated user-mode processes without changing drivers.

namespace NeutrinoOS.Drivers
{
    /// <summary>
    /// Callbacks the kernel invokes on a driver host: device arrival,
    /// departure and interrupts.
    /// </summary>
    public interface IDriverHostServices
    {
        /// <summary>A device was added (or matched at boot) for this host.</summary>
        void OnDeviceAdded(DeviceInfo device);

        /// <summary>A device was removed (hot-unplug or shutdown).</summary>
        void OnDeviceRemoved(DeviceInfo device);

        /// <summary>An interrupt fired on a registered IRQ line.</summary>
        void OnInterrupt(int irq);
    }

    /// <summary>
    /// A host that runs drivers. The kernel driver manager creates one host
    /// per driver group (or per driver for isolation), initializes it, then
    /// enumerates <see cref="Drivers"/> to match devices.
    /// </summary>
    public interface IDriverHost
    {
        /// <summary>Host name used in logs, e.g. "host:virtio-net".</summary>
        string Name { get; }

        /// <summary>
        /// Initialize the host with kernel services and host callbacks.
        /// Called once before any driver is matched.
        /// </summary>
        bool Initialize(IDriverServices services, IDriverHostServices callbacks);

        /// <summary>
        /// The drivers this host can run. Called after Initialize.
        /// </summary>
        IDriver[] GetDrivers();

        /// <summary>Shut the host down: stop all drivers and release resources.</summary>
        void Shutdown();
    }
}
