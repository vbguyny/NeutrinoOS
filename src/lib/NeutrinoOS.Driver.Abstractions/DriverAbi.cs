// NeutrinoOS driver framework - ABI versioning.
//
// The driver ABI is the set of interfaces in this assembly. It is versioned
// with semantic versioning: incompatible interface changes bump the major
// version, additive changes bump the minor version. Drivers declare the ABI
// version they were built against (see IDriver.AbiVersion); the driver
// manager rejects drivers whose major version differs, and warns on older
// minors.

namespace NeutrinoOS.Drivers
{
    /// <summary>Version constants and compatibility helpers for the driver ABI.</summary>
    public static class DriverAbi
    {
        /// <summary>Major ABI version. Incompatible changes increment this.</summary>
        public const int Major = 1;

        /// <summary>Minor ABI version. Additive changes increment this.</summary>
        public const int Minor = 0;

        /// <summary>Human-readable ABI version ("major.minor").</summary>
        public const string VersionString = "1.0";

        /// <summary>
        /// True when a driver built against (major, minor) can run on this kernel.
        /// Major must match exactly; the driver's minor must not be newer than
        /// the kernel's.
        /// </summary>
        public static bool IsCompatible(int major, int minor)
        {
            return major == Major && minor <= Minor;
        }
    }
}
