// NeutrinoOS korlib - System.AppContext minimal implementation.
// Used by framework libraries (e.g. System.SR resource-string helpers) to query
// AppContext switches. NeutrinoOS defines no switches, so every lookup reports
// "not enabled" and callers fall back to their default behavior.
namespace System
{
    public static class AppContext
    {
        /// <summary>Base directory of the application - NeutrinoOS has no filesystem cwd concept.</summary>
        public static string BaseDirectory => "/";

        /// <summary>Target framework moniker string reported to libraries.</summary>
        public static string TargetFrameworkName => "NeutrinoOS,Version=v1.0";

        /// <summary>
        /// Look up the value of a switch. NeutrinoOS defines no switches: always returns
        /// false with isEnabled = false, which selects the libraries' default paths.
        /// </summary>
        public static bool TryGetSwitch(string switchName, out bool isEnabled)
        {
            isEnabled = false;
            return false;
        }

        /// <summary>Named data slot lookup - none defined on NeutrinoOS.</summary>
        public static object? GetData(string name) => null;

        /// <summary>Set a named data slot - no-op on NeutrinoOS.</summary>
        public static void SetData(string name, object? data)
        {
        }
    }
}
