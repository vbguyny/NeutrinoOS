// NeutrinoOS driver framework ABI - host-side device marshaling (Phase 8).
//
// Packaged drivers are JIT-compiled: their IL is compiled against this
// assembly's copy on the image (/lib/NeutrinoOS.Driver.Abstractions.dll),
// and the JIT's layout model for the ABI types is what the driver's field
// access uses. Kernel DeviceInfo objects are bflat-AOT objects with the
// kernel's layout, so handing one to a JIT'd driver can read wrong values.
//
// DeviceInfoMarshal avoids that entirely: the kernel calls these factories
// through the JIT (the methods are lazy-compiled like any other /lib
// method) to build a driver-world DeviceInfo copy carrying the same values.
// Kernel strings are safe to store in it (cross-world string usage is the
// proven path - the kernel already passes its own strings into JIT'd
// entry points), and the returned object lives wholly in the driver's
// world, so every read in the driver uses matching offsets.

using System;

namespace NeutrinoOS.Drivers
{
    /// <summary>
    /// Phase 8: factories that construct driver-world <see cref="DeviceInfo"/>
    /// copies for packaged (JIT-compiled) drivers. Called by the kernel
    /// driver-package loader through compiled thunks with primitive
    /// arguments only (4 and 3 parameters, so the calls stay within the
    /// register-passing part of the calling convention).
    /// </summary>
    public static class DeviceInfoMarshal
    {
        /// <summary>
        /// Phase 8: create a base device copy (match fields are filled in by
        /// <see cref="WithMatch"/>). Uses the four-argument marshal
        /// constructor; resources read as empty.
        /// </summary>
        public static DeviceInfo Create(int id, int parentId, string path, string bus)
        {
            return new DeviceInfo(id, parentId, path, bus);
        }

        /// <summary>
        /// Phase 8: create a device copy that carries the match fields.
        /// <paramref name="vendorDeviceClass"/> packs
        /// vendorId (bits 48-63), deviceId (bits 32-47) and the device class
        /// (bits 24-31); <paramref name="addressClassCode"/> packs the bus
        /// address (bits 0-31) and the raw class code (bits 32-63).
        /// </summary>
        public static DeviceInfo WithMatch(DeviceInfo source, ulong vendorDeviceClass, ulong addressClassCode)
        {
            return new DeviceInfo(source, vendorDeviceClass, addressClassCode);
        }
    }
}
