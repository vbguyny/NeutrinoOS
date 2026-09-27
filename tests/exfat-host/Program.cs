// NeutrinoOS Phase 10 - host-side exFAT test harness (WSL/desktop .NET).
//
// Runs the DDK exFAT driver against a raw image file through a
// file-backed IBlockDevice. Used for fast correctness iteration and for
// the interoperability suite (volumes created by Linux mkfs.exfat); the
// QEMU acceptance suite exercises the same code inside the kernel.
//
// usage: exfat-host <image> <command> [args...]
//   probe                       - boot signature + checksum validation
//   list <path>                 - directory listing
//   cat <path>                  - print a file
//   write <path> <text>         - create/overwrite with text
//   append <path> <text>        - append text
//   mkdir <path>                - create a directory
//   rmdir <path>                - remove an empty directory
//   rm <path>                   - delete a file
//   mv <old> <new>              - rename
//   stat <path>                 - file information
//   big <path> <mb>             - write N MiB pattern, read back, verify
//   fill <dir> <count>          - create N small files
//   longname <dir>              - create/rename/delete a 255-char name
//   fsck [repair]               - run the DDK check/repair engine
//   label [text]                - read (no arg) or set the volume label
//   attrib <path> <spec>        - apply +r/-h style attribute changes
//   format <bytes-mb> [label]   - format the image as exFAT first
//   ro <command...>             - mount read-only and run the command

using System;
using ProtonOS.DDK.Storage;
using ProtonOS.DDK.Storage.ExFat;
using SysFile = System.IO;

namespace ExFatHost;

/// <summary>File-backed block device for the host harness.</summary>
public sealed unsafe class FileBlockDevice : IBlockDevice
{
    private readonly SysFile.FileStream _stream;

    /// <summary>Opens an image file (read-write).</summary>
    /// <param name="path">Image path.</param>
    /// <param name="readOnly">Open read-only.</param>
    public FileBlockDevice(string path, bool readOnly)
    {
        _stream = new SysFile.FileStream(path,
            SysFile.FileMode.Open,
            readOnly ? SysFile.FileAccess.Read : SysFile.FileAccess.ReadWrite,
            SysFile.FileShare.Read);
        BlockSize = 512;
        BlockCount = (ulong)(_stream.Length / BlockSize);
    }

    /// <summary>Driver interface plumbing.</summary>
    public string DriverName => "host-file";

    /// <summary>Version.</summary>
    public Version DriverVersion => new Version(1, 0, 0);

    /// <summary>Device type.</summary>
    public ProtonOS.DDK.Drivers.DriverType Type => ProtonOS.DDK.Drivers.DriverType.Storage;

    /// <summary>State.</summary>
    public ProtonOS.DDK.Drivers.DriverState State => ProtonOS.DDK.Drivers.DriverState.Running;

    /// <summary>No-op.</summary>
    public bool Initialize() => true;

    /// <summary>No-op.</summary>
    public void Shutdown() { }

    /// <summary>No-op.</summary>
    public void Suspend() { }

    /// <summary>No-op.</summary>
    public void Resume() { }

    /// <summary>Name.</summary>
    public string DeviceName => "host-file";

    /// <summary>Total sectors.</summary>
    public ulong BlockCount { get; }

    /// <summary>Sector size.</summary>
    public uint BlockSize { get; }

    /// <summary>Capabilities.</summary>
    public BlockDeviceCapabilities Capabilities =>
        BlockDeviceCapabilities.Read | BlockDeviceCapabilities.Write | BlockDeviceCapabilities.Flush;

    /// <summary>Reads sectors.</summary>
    public int Read(ulong startBlock, uint blockCount, byte* buffer)
    {
        _stream.Seek((long)(startBlock * BlockSize), SysFile.SeekOrigin.Begin);
        long total = (long)blockCount * BlockSize;
        var span = new Span<byte>(buffer, (int)total);
        int done = 0;
        while (done < total)
        {
            int got = _stream.Read(span.Slice(done));
            if (got <= 0)
                return -3;
            done += got;
        }
        return (int)blockCount;
    }

    /// <summary>Writes sectors.</summary>
    public int Write(ulong startBlock, uint blockCount, byte* buffer)
    {
        _stream.Seek((long)(startBlock * BlockSize), SysFile.SeekOrigin.Begin);
        long total = (long)blockCount * BlockSize;
        var span = new ReadOnlySpan<byte>(buffer, (int)total);
        _stream.Write(span);
        return (int)blockCount;
    }

    /// <summary>Flushes.</summary>
    public BlockResult Flush()
    {
        _stream.Flush(true);
        return BlockResult.Success;
    }

    /// <summary>Closes the file.</summary>
    public void Close() => _stream.Dispose();
}

/// <summary>Host harness entry point.</summary>
public static class Program
{
    private static int _failures;

    /// <summary>Runs one command.</summary>
    public static int Main(string[] args)
    {
        ExFatScratch.UseManaged = true;
        ExFatScratch.Backend = new ManagedScratchBackend();
        ExFatClock.OverrideNowEpoch = 1780000000;   // fixed test clock (UTC)

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: exfat-host <image> <command> [args]");
            return 2;
        }

        string image = args[0];
        string cmd = args[1];

        if (cmd == "format")
        {
            int mb = args.Length >= 3 ? int.Parse(args[2]) : 16;
            string label = args.Length >= 4 ? args[3] : "";
            var f = new SysFile.FileStream(image, SysFile.FileMode.Create, SysFile.FileAccess.ReadWrite);
            f.SetLength((long)mb * 1024 * 1024);
            f.Close();
            var dev0 = new FileBlockDevice(image, false);
            int rc0 = ExFatFormatter.Format(dev0, new ExFatFormatOptions { Label = label });
            dev0.Close();
            Console.WriteLine(rc0 == 0 ? "format OK" : "format FAILED rc=" + rc0);
            return rc0 == 0 ? 0 : 1;
        }

        bool readOnly = cmd == "ro";
        if (readOnly)
        {
            cmd = args[2];
            var rest = new string[args.Length - 3];
            Array.Copy(args, 3, rest, 0, rest.Length);
            args = rest;
        }

        if (cmd == "fsck")
        {
            bool repair = args.Length > 2 && args[2] == "repair";
            var fdev = new FileBlockDevice(image, !repair);
            var result = new ExFatFsckResult();
            int frc = ExFatFsck.Run(fdev, repair, result);
            Console.Write(result.Log.ToString());
            Console.WriteLine("fsck: errors=" + result.Errors + " repaired=" + result.Repaired +
                " unrepaired=" + result.Unrepaired + " warnings=" + result.Warnings);
            Console.WriteLine(frc == 0 ? "FSCK: CLEAN" : "FSCK: PROBLEMS");
            fdev.Close();
            return frc;
        }

        if (cmd == "label")
        {
            var ldev = new FileBlockDevice(image, false);
            if (args.Length <= 2)
            {
                string lbl = ExFatLabel.Get(ldev) ?? "<unreadable>";
                Console.WriteLine("label=\"" + lbl + "\"");
                ldev.Close();
                return 0;
            }
            int lrc = ExFatLabel.Set(ldev, args[2]);
            Console.WriteLine(lrc == 0 ? "label set OK" : "label set FAILED rc=" + lrc);
            ldev.Close();
            return lrc == 0 ? 0 : 1;
        }

        if (cmd == "attrib")
        {
            var adev = new FileBlockDevice(image, false);
            int arc = ExFatAttrib.Apply(adev, args[2], args[3]);
            Console.WriteLine(arc == 0 ? "attrib OK" : "attrib FAILED rc=" + arc);
            adev.Close();
            return arc == 0 ? 0 : 1;
        }

        var dev = new FileBlockDevice(image, readOnly);
        var fs = new ExFatFileSystem();
        fs.Initialize();

        if (!fs.Probe(dev))
        {
            Console.Error.WriteLine("probe: not an exFAT volume");
            return 1;
        }
        Console.WriteLine("probe OK");
        Console.Out.Flush();

        FileResult mrc = fs.Mount(dev, readOnly);
        if (mrc != FileResult.Success)
        {
            Console.Error.WriteLine("mount failed: " + mrc);
            return 1;
        }
        Console.WriteLine("mount OK label=\"" + (fs.VolumeLabel ?? "") + "\" total=" +
            fs.TotalBytes + " free=" + fs.FreeBytes);
        Console.Out.Flush();

        int rc;
        switch (cmd)
        {
            case "probe":
                rc = 0;
                break;
            case "list": rc = CmdList(fs, args.Length > 2 ? args[2] : "/"); break;
            case "cat": rc = CmdCat(fs, args[2]); break;
            case "write": rc = CmdWrite(fs, args[2], Join(args, 3), false); break;
            case "append": rc = CmdWrite(fs, args[2], Join(args, 3), true); break;
            case "mkdir": rc = CmdMkdir(fs, args[2]); break;
            case "rmdir": rc = CmdRmdir(fs, args[2]); break;
            case "rm": rc = CmdRm(fs, args[2]); break;
            case "mv": rc = CmdMv(fs, args[2], args[3]); break;
            case "stat": rc = CmdStat(fs, args[2]); break;
            case "big": rc = CmdBig(fs, args[2], int.Parse(args[3])); break;
            case "fill": rc = CmdFill(fs, args[2], int.Parse(args[3])); break;
            case "longname": rc = CmdLongName(fs, args[2]); break;
            default:
                Console.Error.WriteLine("unknown command: " + cmd);
                rc = 2;
                break;
        }

        FileResult urc = fs.Unmount();
        if (urc != FileResult.Success)
            Console.Error.WriteLine("unmount failed: " + urc);
        fs.Shutdown();
        dev.Close();

        if (_failures > 0)
        {
            Console.Error.WriteLine("HOST: " + _failures + " FAIL");
            return 1;
        }
        Console.WriteLine("HOST: OK");
        return rc;
    }

    private static string Join(string[] args, int from)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = from; i < args.Length; i++)
        {
            if (i > from) sb.Append(' ');
            sb.Append(args[i]);
        }
        return sb.ToString();
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name + (detail.Length > 0 ? " (" + detail + ")" : ""));
        if (!ok) _failures++;
    }

    private static string ReadAll(ExFatFileSystem fs, string path)
    {
        IFileHandle? h;
        var rc = fs.OpenFile(path, ProtonOS.DDK.Storage.FileMode.Open, ProtonOS.DDK.Storage.FileAccess.Read, out h);
        if (rc != FileResult.Success || h == null)
            return "\u0001ERR:" + rc;
        var sb = new System.Text.StringBuilder();
        byte[] buf = new byte[4096];
        while (true)
        {
            int got;
            unsafe
            {
                fixed (byte* p = buf)
                    got = h.Read(p, buf.Length);
            }
            if (got <= 0)
                break;
            for (int i = 0; i < got; i++)
                sb.Append((char)buf[i]);
        }
        h.Dispose();
        return sb.ToString();
    }

    private static int WriteAll(ExFatFileSystem fs, string path, byte[] data, bool append)
    {
        IFileHandle? h;
        var mode = append ? ProtonOS.DDK.Storage.FileMode.Append : ProtonOS.DDK.Storage.FileMode.Create;
        var rc = fs.OpenFile(path, mode, ProtonOS.DDK.Storage.FileAccess.Write, out h);
        if (rc != FileResult.Success || h == null)
            return -(int)rc;
        unsafe
        {
            fixed (byte* p = data)
            {
                int written = h.Write(p, data.Length);
                if (written != data.Length)
                {
                    h.Dispose();
                    return -7;
                }
            }
        }
        h.Dispose();
        return 0;
    }

    private static int CmdList(ExFatFileSystem fs, string path)
    {
        IDirectoryHandle? dir;
        var rc = fs.OpenDirectory(path, out dir);
        if (rc != FileResult.Success || dir == null)
        {
            Check("list " + path, false, rc.ToString());
            return 1;
        }
        int count = 0;
        FileInfo? e;
        while ((e = dir.ReadNext()) != null)
        {
            Console.WriteLine("  " + (e.IsDirectory ? "d" : "-") + " " + e.Size + "  " + e.Name);
            count++;
        }
        dir.Dispose();
        Console.WriteLine("entries=" + count);
        return 0;
    }

    private static int CmdCat(ExFatFileSystem fs, string path)
    {
        string content = ReadAll(fs, path);
        if (content.StartsWith("\u0001ERR:"))
        {
            Check("cat " + path, false, content);
            return 1;
        }
        Console.Write(content);
        if (content.Length == 0 || content[content.Length - 1] != '\n')
            Console.WriteLine();
        return 0;
    }

    private static int CmdWrite(ExFatFileSystem fs, string path, string text, bool append)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = (byte)text[i];
        int rc = WriteAll(fs, path, bytes, append);
        Check((append ? "append " : "write ") + path, rc == 0, "rc=" + rc);
        if (rc == 0)
        {
            string back = ReadAll(fs, path);
            Check("readback " + path, back.EndsWith(text), "len=" + back.Length);
        }
        return rc == 0 ? 0 : 1;
    }

    private static int CmdMkdir(ExFatFileSystem fs, string path)
    {
        var rc = fs.CreateDirectory(path);
        Check("mkdir " + path, rc == FileResult.Success, rc.ToString());
        return rc == FileResult.Success ? 0 : 1;
    }

    private static int CmdRmdir(ExFatFileSystem fs, string path)
    {
        var rc = fs.DeleteDirectory(path);
        Check("rmdir " + path, rc == FileResult.Success, rc.ToString());
        return rc == FileResult.Success ? 0 : 1;
    }

    private static int CmdRm(ExFatFileSystem fs, string path)
    {
        var rc = fs.DeleteFile(path);
        Check("rm " + path, rc == FileResult.Success, rc.ToString());
        return rc == FileResult.Success ? 0 : 1;
    }

    private static int CmdMv(ExFatFileSystem fs, string oldPath, string newPath)
    {
        var rc = fs.Rename(oldPath, newPath);
        Check("mv " + oldPath + " -> " + newPath, rc == FileResult.Success, rc.ToString());
        if (rc == FileResult.Success)
        {
            Check("renamed exists", fs.Exists(newPath));
            Check("old gone", !fs.Exists(oldPath));
        }
        return rc == FileResult.Success ? 0 : 1;
    }

    private static int CmdStat(ExFatFileSystem fs, string path)
    {
        FileInfo? info;
        var rc = fs.GetInfo(path, out info);
        Check("stat " + path, rc == FileResult.Success && info != null, rc.ToString());
        if (info != null)
            Console.WriteLine("  name=" + info.Name + " type=" + info.Type + " size=" + info.Size +
                " attrs=" + info.Attributes + " mtime=" + info.ModificationTime);
        return rc == FileResult.Success ? 0 : 1;
    }

    private static int CmdBig(ExFatFileSystem fs, string path, int mb)
    {
        // 64-bit chunked so >4 GiB files work (the managed full-file
        // buffer would otherwise overflow the size math).
        long size = (long)mb * 1024 * 1024;
        const int Chunk = 8 * 1024 * 1024;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IFileHandle? w;
        var wrc = fs.OpenFile(path, ProtonOS.DDK.Storage.FileMode.Create,
            ProtonOS.DDK.Storage.FileAccess.Write, out w);
        bool ok = wrc == FileResult.Success && w != null;
        if (!ok)
        {
            Check("big open (write)", false, wrc.ToString());
            return 1;
        }
        // Pattern buffer: ((offset*31+7) & 0xFF) repeats with period 256
        // and every chunk size is a multiple of 256, so one filled buffer
        // serves every chunk (filling 4.5 GiB byte-by-byte in a Debug
        // build dominated the write time otherwise).
        var chunkBuf = new byte[Chunk];
        for (int i = 0; i < Chunk; i++)
            chunkBuf[i] = (byte)((i * 31 + 7) & 0xFF);
        long written = 0;
        int chunkNo = 0;
        while (written < size)
        {
            int n = Chunk;
            if (size - written < Chunk)
                n = (int)(size - written);   // clamp first: >= 2 GiB must not wrap
            int wrote;
            unsafe
            {
                fixed (byte* p = chunkBuf)
                    wrote = w!.Write(p, n);
            }
            if (wrote != n)
            {
                Console.Error.WriteLine("write stalled at " + written + " (wrote=" + wrote + ")");
                ok = false;
                break;
            }
            written += wrote;
            chunkNo++;
            if (chunkNo % 64 == 0)
                Console.Error.WriteLine("  write " + (written >> 20) + " / " + (size >> 20) + " MiB");
        }
        w!.Dispose();
        sw.Stop();
        Check("big write " + mb + " MiB", ok, "bytes=" + written + " ms=" + sw.ElapsedMilliseconds);
        if (!ok)
            return 1;
        Console.WriteLine("write MB/s=" + (mb * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds)));

        IFileHandle? h;
        var orc = fs.OpenFile(path, ProtonOS.DDK.Storage.FileMode.Open, ProtonOS.DDK.Storage.FileAccess.Read, out h);
        if (orc != FileResult.Success || h == null)
        {
            Check("big open", false, orc.ToString());
            return 1;
        }
        sw.Restart();
        var buf = new byte[65536];
        long total = 0;
        ok = true;
        long lastReport = 0;
        while (true)
        {
            int got;
            unsafe
            {
                fixed (byte* p = buf)
                    got = h.Read(p, buf.Length);
            }
            if (got <= 0)
                break;
            for (int i = 0; i < got; i++)
            {
                // Compare against the chunk pattern (period 256 lines up
                // because read starts are 64 KiB aligned).
                if (buf[i] != chunkBuf[(int)((total + i) & (Chunk - 1))])
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                break;
            total += got;
            if (total - lastReport >= 32 * 1024 * 1024)
            {
                lastReport = total;
                Console.Error.WriteLine("  verify " + (total >> 20) + " / " + (size >> 20) + " MiB");
            }
        }
        h.Dispose();
        sw.Stop();
        Check("big read " + total + " bytes verify", ok && total == size);
        Console.WriteLine("read MB/s=" + (mb * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds)));
        return ok && total == size ? 0 : 1;
    }

    private static int CmdFill(ExFatFileSystem fs, string dir, int count)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            string name = dir + "/f" + i + ".txt";
            var bytes = new byte[100];
            for (int b = 0; b < bytes.Length; b++)
                bytes[b] = (byte)('a' + (i % 26));
            int rc = WriteAll(fs, name, bytes, false);
            if (rc != 0)
            {
                Check("fill file " + i, false, "rc=" + rc);
                return 1;
            }
        }
        sw.Stop();
        Console.WriteLine("created " + count + " files in " + sw.ElapsedMilliseconds + " ms");

        IDirectoryHandle? dh;
        var orc = fs.OpenDirectory(dir, out dh);
        if (orc != FileResult.Success || dh == null)
        {
            Check("fill list", false, orc.ToString());
            return 1;
        }
        int seen = 0;
        while (dh.ReadNext() != null)
            seen++;
        dh.Dispose();
        Check("fill listed " + seen + "/" + count, seen == count);
        return seen == count ? 0 : 1;
    }

    private static int CmdLongName(ExFatFileSystem fs, string dir)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; sb.Length < 255; i++)
            sb.Append((char)('a' + (i % 26)));
        string longName = sb.ToString().Substring(0, 255);
        string path = dir + "/" + longName;

        string content = "long-name-content";
        var bytes = new byte[content.Length];
        for (int i = 0; i < content.Length; i++)
            bytes[i] = (byte)content[i];
        int rc = WriteAll(fs, path, bytes, false);
        Check("create 255-char name", rc == 0, "rc=" + rc);
        if (rc != 0)
            return 1;

        string back = ReadAll(fs, path);
        Check("read 255-char name", back == content);
        Check("stat 255-char name", fs.Exists(path));

        string path2 = dir + "/" + longName.Substring(0, 250) + "XyZ";
        var mrc = fs.Rename(path, path2);
        Check("rename 255-char name", mrc == FileResult.Success, mrc.ToString());

        var drc = fs.DeleteFile(path2);
        Check("delete 255-char name", drc == FileResult.Success, drc.ToString());
        Check("gone after delete", !fs.Exists(path2));
        return 0;
    }
}
