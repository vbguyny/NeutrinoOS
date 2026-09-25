// NeutrinoOS driver framework - driver interfaces.
//
// A driver is a managed class implementing IDriver. It is discovered from a
// driver assembly (built-in from /drivers, or packaged from
// /var/lib/npkg/drivers). The driver manager instantiates it, matches it
// against device-tree entries using Match(), then calls Probe()/Start().

namespace NeutrinoOS.Drivers
{
    /// <summary>
    /// A device driver. Lifecycle: constructed -> Match(device) -> Probe(device)
    /// -> Start(device) -> [running] -> Stop(device) -> (disposed).
    /// </summary>
    public interface IDriver
    {
        /// <summary>Human-readable driver name, e.g. "virtio-net".</summary>
        string Name { get; }

        /// <summary>Driver semantic version, e.g. "1.2.0".</summary>
        string Version { get; }

        /// <summary>Driver ABI major version this driver was built against.</summary>
        int AbiMajor { get; }

        /// <summary>Driver ABI minor version this driver was built against.</summary>
        int AbiMinor { get; }

        /// <summary>
        /// Cheap match test: can this driver handle the device? Called for
        /// every device in the tree; must not touch hardware.
        /// </summary>
        bool Match(DeviceInfo device);

        /// <summary>
        /// Probe the device (identify exact variant, validate resources).
        /// Called once Match() returned true. Return false to defer to
        /// other drivers.
        /// </summary>
        bool Probe(DeviceInfo device);

        /// <summary>
        /// Start the device: map resources, register interrupts, bring the
        /// hardware to a working state. Return false on failure.
        /// </summary>
        bool Start(DeviceInfo device);

        /// <summary>Stop the device and release resources. Called on removal.</summary>
        void Stop(DeviceInfo device);
    }
}
