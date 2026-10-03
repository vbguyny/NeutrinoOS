// NeutrinoOS Phase 5 utility: hexdump - hex dump of files
//
// usage: hexdump [-n bytes] file...
//
//   -n bytes   dump at most the first <bytes> bytes of each file
//
// Prints 16 bytes per line: file offset, hex bytes (a gap in the
// middle) and the printable-ASCII rendering. Handy for inspecting
// assemblies and disk images from the shell.

using System;
using System.Text;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Hexdump;

/// <summary>The hexdump utility (see file header).</summary>
public static class Program
{
    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        int limit = 0;      // 0 = no limit
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: hexdump [-n bytes] file...",
                    "  -n bytes   dump at most the first <bytes> bytes",
                    "  Prints offset, hex bytes and ASCII, 16 bytes per line.");
            }
            if (a == "-n")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("hexdump", "-n: missing byte count");
                if (!Util.TryParseInt(args[i + 1], out limit) || limit <= 0)
                    return Util.Fail("hexdump", args[i + 1] + ": invalid byte count");
                i++;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("hexdump", a + ": unknown option");
            }
            else
            {
                files.Add(a);
            }
        }

        if (files.Count == 0)
            return Util.Fail("hexdump", "missing file operand");

        int rc = 0;
        for (int f = 0; f < files.Count; f++)
        {
            if (!System.IO.File.Exists(files[f]))
            {
                rc = Util.Fail("hexdump", files[f] + ": no such file");
                continue;
            }
            if (files.Count > 1)
                Console.WriteLine(":::::::: " + files[f] + " ::::::::");

            byte[] data;
            try
            {
                data = System.IO.File.ReadAllBytes(files[f]);
            }
            catch (Exception)
            {
                rc = Util.Fail("hexdump", files[f] + ": read failed");
                continue;
            }

            int len = data.Length;
            if (limit > 0 && limit < len)
                len = limit;

            for (int off = 0; off < len; off += 16)
                DumpRow(data, off, len);
        }
        return rc;
    }

    /// <summary>Prints one 16-byte row at <paramref name="off"/>.</summary>
    private static void DumpRow(byte[] data, int off, int len)
    {
        var sb = new StringBuilder();
        AppendHex32(sb, off);
        sb.Append("  ");

        for (int i = 0; i < 16; i++)
        {
            if (i == 8)
                sb.Append(' ');
            if (off + i < len)
            {
                sb.Append(HexDigits[(data[off + i] >> 4) & 0xF]);
                sb.Append(HexDigits[data[off + i] & 0xF]);
                sb.Append(' ');
            }
            else
            {
                sb.Append("   ");
            }
        }

        sb.Append(" |");
        for (int i = 0; i < 16 && off + i < len; i++)
        {
            byte b = data[off + i];
            sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
        }
        sb.Append('|');
        Console.WriteLine(sb.ToString());
    }

    /// <summary>Appends a value as 8 hex digits.</summary>
    private static void AppendHex32(StringBuilder sb, int value)
    {
        for (int shift = 28; shift >= 0; shift -= 4)
            sb.Append(HexDigits[(value >> shift) & 0xF]);
    }
}
