// NeutrinoOS Phase 10 - USB auto-mount service (Task 4).
//
// Polled from the kernel's shell idle hook (see AutoMountBridge in the
// kernel): when a removable disk appears in the block device registry
// that carries an exFAT volume, it is mounted at /mnt/usb/<device>;
// when the disk goes away the volume is unmounted. Scans are gated on
// the registry's attach/detach generation counter, so the service is
// cheap while idle:
//
//   [automount] /dev/sda mounted at /mnt/usb/sda (exFAT, label "STICK")
//   [automount] /dev/sda removed; unmounted /mnt/usb/sda
//
// The mount goes through the DDK VFS, so every utility (ls, cat, df,
// mount) sees it. Only removable devices auto-mount; fixed disks are
// mounted explicitly (mount -t exfat <device> <path>) - the boot AHCI
// disk keeps its driver-owned mounts.
//
// The service also feeds the kernel's shell-completion shadow list via
// the Kernel_AutoMountNote/Forget exports (device name + mount path).

using System;
using System.Runtime.InteropServices;

namespace ProtonOS.DDK.Storage.ExFat;

/// <summary>USB exFAT auto-mount service (see file header).</summary>
public static unsafe class AutoMount
{
    private const int MaxTracked = 8;

    // Tracked mounts: handle stays valid while the disk is present.
    private static readonly int[] _handles = new int[MaxTracked];
    private static readonly string?[] _names = new string?[MaxTracked];
    private static readonly string?[] _paths = new string?[MaxTracked];
    private static int _count;

    // Last registry generation observed (scan gate).
    private static int _lastGeneration = -1;

    /// <summary>Kernel completion shadow: records a mounted volume.</summary>
    [DllImport("*", EntryPoint = "Kernel_AutoMountNote")]
    private static extern int AutoMountNote(char* device, int deviceLen, char* path, int pathLen);

    /// <summary>Kernel completion shadow: drops a volume record.</summary>
    [DllImport("*", EntryPoint = "Kernel_AutoMountForget")]
    private static extern int AutoMountForget(char* device, int deviceLen);

    /// <summary>
    /// One poll step: detach handling, then (on registry changes) an
    /// attach scan. Returns the number of tracked mounts.
    /// </summary>
    public static int Tick()
    {
        HandleRemovals();

        int generation = BlockDevices.Generation;
        if (generation != _lastGeneration)
        {
            _lastGeneration = generation;
            ScanForNewDevices();
        }
        return _count;
    }

    /// <summary>Unmounts tracked volumes whose devices disappeared.</summary>
    private static void HandleRemovals()
    {
        int i = 0;
        while (i < _count)
        {
            if (BlockDevices.IsPresent(_handles[i]))
            {
                i++;
                continue;
            }

            string name = _names[i] ?? "";
            string path = _paths[i] ?? "";
            if (path.Length > 0)
            {
                VFS.Unmount(path);
                Console.Write("[automount] /dev/");
                Console.Write(name);
                Console.Write(" removed; unmounted ");
                Console.WriteLine(path);
            }
            ForgetNote(name);

            // Drop the entry (order preserved).
            for (int j = i; j < _count - 1; j++)
            {
                _handles[j] = _handles[j + 1];
                _names[j] = _names[j + 1];
                _paths[j] = _paths[j + 1];
            }
            _count--;
            _handles[_count] = 0;
            _names[_count] = null;
            _paths[_count] = null;
        }
    }

    /// <summary>Probes untracked removable devices and mounts exFAT ones.</summary>
    private static void ScanForNewDevices()
    {
        var all = BlockDevices.Enumerate();
        for (int i = 0; i < all.Length; i++)
        {
            var info = all[i];
            if (!info.Removable)
                continue;
            if (IsTracked(info.Handle))
                continue;
            TryMount(info);
        }
    }

    /// <summary>True when a device handle is already tracked.</summary>
    private static bool IsTracked(int handle)
    {
        for (int i = 0; i < _count; i++)
            if (_handles[i] == handle)
                return true;
        return false;
    }

    /// <summary>Mounts one removable device when it holds exFAT.</summary>
    private static void TryMount(BlockDeviceInfo info)
    {
        if (_count >= MaxTracked)
            return;

        var dev = new KernelBlockDevice(info);
        var fs = new ExFatFileSystem();
        fs.Initialize();

        if (!fs.Probe(dev))
        {
            fs.Shutdown();
            Console.Write("[automount] /dev/");
            Console.Write(info.Name);
            Console.WriteLine(": no exFAT signature (not mounted)");
            return;
        }

        // Mount point: /mnt/usb/<device> (created on demand; best effort -
        // the directory entries are cosmetic, the VFS mount is what counts).
        VFS.CreateDirectory("/mnt");
        VFS.CreateDirectory("/mnt/usb");
        string mountPath = "/mnt/usb/" + info.Name;
        VFS.CreateDirectory(mountPath);

        // VFS.Mount performs the filesystem mount itself.
        var mountResult = VFS.Mount(mountPath, fs, dev, false);
        if (mountResult != FileResult.Success)
        {
            fs.Shutdown();
            Console.Write("[automount] /dev/");
            Console.Write(info.Name);
            Console.Write(": VFS mount at ");
            Console.Write(mountPath);
            Console.WriteLine(" failed");
            return;
        }

        _handles[_count] = info.Handle;
        _names[_count] = info.Name;
        _paths[_count] = mountPath;
        _count++;

        NoteNote(info.Name, mountPath);

        Console.Write("[automount] /dev/");
        Console.Write(info.Name);
        Console.Write(" mounted at ");
        Console.Write(mountPath);
        Console.Write(" (exFAT");
        if (fs.VolumeLabel != null && fs.VolumeLabel.Length > 0)
        {
            Console.Write(", label \"");
            Console.Write(fs.VolumeLabel);
            Console.Write("\"");
        }
        Console.Write(", ");
        Console.Write((long)(fs.FreeBytes / 1024));
        Console.WriteLine(" KB free)");
    }

    /// <summary>Records the mount in the kernel completion shadow.</summary>
    private static void NoteNote(string device, string mountPath)
    {
        const int Cap = 64;
        char* devBuf = stackalloc char[Cap];
        char* pathBuf = stackalloc char[Cap];
        int devLen = device.Length > Cap ? Cap : device.Length;
        int pathLen = mountPath.Length > Cap ? Cap : mountPath.Length;
        for (int i = 0; i < devLen; i++)
            devBuf[i] = device[i];
        for (int i = 0; i < pathLen; i++)
            pathBuf[i] = mountPath[i];
        AutoMountNote(devBuf, devLen, pathBuf, pathLen);
    }

    /// <summary>Drops the mount from the kernel completion shadow.</summary>
    private static void ForgetNote(string device)
    {
        const int Cap = 64;
        char* devBuf = stackalloc char[Cap];
        int devLen = device.Length > Cap ? Cap : device.Length;
        for (int i = 0; i < devLen; i++)
            devBuf[i] = device[i];
        AutoMountForget(devBuf, devLen);
    }
}
