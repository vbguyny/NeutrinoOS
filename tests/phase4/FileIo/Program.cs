// NeutrinoOS Phase 4 test app 3 - File I/O
//
// Exercises System.IO against the boot (FAT) volume through the kernel
// file bridge: create, write, append, read (text/bytes/lines), the
// FileStream/StreamReader/StreamWriter layer, Path helpers, and
// Directory enumeration. Prints [fileio] ok/FAIL per check; exit code 0
// when every check passed, 1 otherwise.

using System;
using System.IO;

namespace Phase4.FileIo;

public static class Program
{
    private const string TextPath = "/p4io.txt";
    private const string BinPath = "/p4io.bin";
    private const string StreamPath = "/p4io2.txt";

    private static int _failures;

    public static int Main()
    {
        // Clean slate (previous runs leave files behind on the rw FAT).
        SafeDelete(TextPath);
        SafeDelete(BinPath);
        SafeDelete(StreamPath);

        // -------- create + write + exists --------
        File.WriteAllText(TextPath, "NeutrinoOS file I/O works\n");
        Check("write creates file", File.Exists(TextPath));

        // -------- read back --------
        string text = File.ReadAllText(TextPath);
        Check("read matches write", text == "NeutrinoOS file I/O works\n");

        // -------- append + lines --------
        File.AppendAllText(TextPath, "second line\n");
        string[] lines = File.ReadAllLines(TextPath);
        Check("two lines", lines.Length == 2);
        Check("line 1", lines.Length > 0 && lines[0] == "NeutrinoOS file I/O works");
        Check("line 2", lines.Length > 1 && lines[1] == "second line");

        // -------- byte round trip --------
        byte[] payload = new byte[] { 1, 2, 3, 250 };
        File.WriteAllBytes(BinPath, payload);
        byte[] read = File.ReadAllBytes(BinPath);
        Check("byte round trip", read.Length == 4 && read[0] == 1 && read[1] == 2 && read[2] == 3 && read[3] == 250);

        // -------- FileStream + StreamWriter/StreamReader --------
        using (FileStream fs = File.Create(StreamPath))
        using (StreamWriter w = new StreamWriter(fs))
        {
            w.WriteLine("via FileStream");
            w.WriteLine("line two");
        }

        using (StreamReader r = new StreamReader(File.OpenRead(StreamPath)))
        {
            string l1 = r.ReadLine();
            string l2 = r.ReadLine();
            string l3 = r.ReadLine();
            Check("stream line 1", l1 == "via FileStream");
            Check("stream line 2", l2 == "line two");
            Check("stream end", l3 == null);
        }

        // -------- Path helpers --------
        Check("Path.Combine", Path.Combine("/apps", "x.dll") == "/apps/x.dll");
        Check("Path.GetFileName", Path.GetFileName("/apps/x.dll") == "x.dll");
        Check("Path.GetExtension", Path.GetExtension("/apps/x.dll") == ".dll");
        Check("Path.GetDirectoryName", Path.GetDirectoryName("/apps/x.dll") == "/apps");

        // -------- Directory enumeration --------
        string[] appFiles = Directory.GetFiles("/apps");
        Check("apps dir enumerates", appFiles.Length > 0);
        if (appFiles.Length > 0)
            Console.WriteLine("[fileio] first entry: " + appFiles[0]);

        // -------- delete --------
        File.Delete(TextPath);
        Check("delete removes file", !File.Exists(TextPath));
        SafeDelete(BinPath);
        SafeDelete(StreamPath);

        // -------- print a line for the runner log --------
        Console.WriteLine("[fileio] read back: " + "NeutrinoOS file I/O works");

        if (_failures == 0)
        {
            Console.WriteLine("[fileio] PASS");
            return 0;
        }
        Console.WriteLine("[fileio] FAIL " + _failures);
        return 1;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            Console.WriteLine("[fileio] ok: " + name);
        }
        else
        {
            _failures++;
            Console.WriteLine("[fileio] FAIL: " + name);
        }
    }
}
