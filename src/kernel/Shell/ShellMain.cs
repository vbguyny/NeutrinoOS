// NeutrinoOS kernel - Phase 5 shell: REPL
//
// The interactive loop: banner, prompt (PS1-aware), read a line from the
// line discipline, record history, execute, and repeat. Ctrl+C cancels
// the current line; Ctrl+D on an empty line (or the exit built-in) ends
// the session. While waiting for input the shell pumps background jobs
// cooperatively (LineDiscipline.IdleHook -> IdlePump -> JobManager.Pump).

using System;
using System.Runtime.InteropServices;
using ProtonOS.Platform;
using ProtonOS.Arch;

namespace ProtonOS.Shell;

/// <summary>The Phase 5 shell REPL (see file header).</summary>
public static unsafe class ShellMain
{
    /// <summary>True while the shell is waiting for a line (idle pump gate).</summary>
    private static bool _waitingForInput;

    /// <summary>
    /// Figlet-style (standard font) launch title, one string per row.
    /// Shown on a freshly cleared console once boot has completed.
    /// </summary>
    private static readonly string[] BannerRows =
    {
        @" _   _ _____ _   _ _____ ____  ___ _   _  ___",
        @"| \ | | ____| | | |_   _|  _ \|_ _| \ | |/ _ \",
        @"|  \| |  _| | | | | | | | |_) || ||  \| | | | |",
        @"| |\  | |___| |_| | | | |  _ < | || |\  | |_| |",
        @"|_| \_|_____|\___/  |_| |_| \_\___|_| \_|\___/",
    };

    /// <summary>
    /// Runs the interactive session. Returns after EOF / exit; the caller
    /// halts the CPU.
    /// </summary>
    public static void Run()
    {
        ShellInit.Initialize();

        // Version = major.minor.build; the build number is stamped by
        // version-bump.sh on every build (see src/kernel/Generated).
        Console.WriteLine("[SHELL] NeutrinoOS console ready (v"
            + NeutrinoVersion.ShortVersion + ").");

        // Boot completed: wipe the boot log from the live console (serial
        // terminals receive ESC[2J ESC[H, the VGA text console is cleared)
        // and show the launch title, then drop into the first prompt.
        Console.Clear();
        for (int i = 0; i < BannerRows.Length; i++)
            Console.WriteLine(BannerRows[i]);
        Console.WriteLine();
        Console.WriteLine(ProtonOS.Exports.DDK.SystemInfoExports.VersionBanner);

        Console.WriteLine("Type 'help' for available commands.");

        // Background jobs execute while the shell waits for input.
        LineDiscipline.IdleHook = &IdlePump;

        while (true)
        {
            Console.Write(ShellInit.BuildPrompt());

            _waitingForInput = true;
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            finally
            {
                _waitingForInput = false;
            }

            if (line == null)
            {
                if (Console.LastReadLineCanceled)
                {
                    // Ctrl+C: fresh prompt.
                    continue;
                }

                // Ctrl+D on an empty line: end of input.
                Console.WriteLine("logout");
                break;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            ShellInit.RecordHistory(trimmed);

            ShellExecutor.ExecuteLine(trimmed);

            if (ShellState.ExitRequested)
            {
                ShellInit.SaveHistoryOnExit();
                Console.WriteLine("logout");
                break;
            }
        }

        CPU.HaltForever();
    }

    /// <summary>
    /// Idle hook installed into the line discipline: runs one queued
    /// background job when the shell - not an application - is waiting
    /// for console input.
    /// </summary>
    [UnmanagedCallersOnly]
    public static void IdlePump()
    {
        if (!_waitingForInput)
            return;
        if (ShellExecutor.InForegroundCommand)
            return;

        // Phase 6: cooperative background services (sshd, webhost).
        Services.ServiceRegistry.Tick();

        // Phase 8: PCIe hot-plug poll (throttled internally to ~200 ms);
        // loads/unloads drivers when devices are added/removed on a slot.
        ProtonOS.Drivers.PcieHotplug.Poll();

        // Phase 9: USB poll - event ring drain (hot-plug), HID interrupt
        // endpoints and CDC-ACM receive drains.
        if (ProtonOS.Usb.UsbStack.ControllerPresent)
        {
            ProtonOS.Usb.UsbStack.Poll();
            ProtonOS.Usb.UsbHid.Poll();
            ProtonOS.Usb.UsbSerial.Poll();
        }

        // Phase 10: auto-mount removable exFAT volumes (USB sticks) at
        // /mnt/usb/<device>; unmounts them when the disk goes away.
        ProtonOS.Platform.AutoMountBridge.Poll();

        if (!JobManager.HasQueuedJobs)
            return;
        JobManager.Pump();
    }
}
