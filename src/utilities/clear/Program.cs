// NeutrinoOS Phase 5 utility: clear - clear the terminal screen
//
// usage: clear
//
// Emits the console clear sequence (ESC[2J ESC[H). The VGA console
// device applies it to the framebuffer directly; a serial terminal
// interprets the same sequence itself, so this works on both consoles.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Clear;

/// <summary>The clear utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: clear",
                "  Clear the terminal screen and home the cursor.");
        }
        if (args.Length > 0)
            return Util.Fail("clear", "usage: clear");

        Console.Clear();
        return 0;
    }
}
