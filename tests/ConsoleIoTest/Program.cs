// NeutrinoOS - Phase 2 console I/O acceptance test (console_io_test.dll)
//
// Runs inside NeutrinoOS via the Tier-0 JIT when the "run-console-test"
// marker file is present on the boot volume. Exercises System.Console
// end-to-end through the CAL, line discipline and serial driver.
//
// The test is interactive: after each "[CONIO] ..." prompt it waits for
// keystrokes from the host. scripts/test-console.ps1 (Windows) /
// build/wsl-conio-runner.py (WSL) drives it deterministically by reading
// these prompts and sending the matching byte sequences.
//
// RunAllTests returns (pass << 16) | fail like the other in-boot suites.

using System;
using System.Text;

namespace ConsoleIoTest;

public static class TestRunner
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine("[CONIO] PASS " + name);
        }
        else
        {
            _fail++;
            Console.WriteLine("[CONIO] FAIL " + name);
        }
    }

    /// <summary>Runs all console I/O tests; returns (pass &lt;&lt; 16) | fail.</summary>
    public static int RunAllTests()
    {
        Console.WriteLine("[CONIO] == Executing (interactive) ==");

        TestOutputBasics();
        TestFormattedOutput();
        TestEncodingsAndEnvironment();
        TestColors();
        TestClearAndCursor();
        TestReadKey();
        TestReadLineEditing();
        TestReadLineCancellation();
        TestHistoryRecall();
        TestEof();
        TestBulkReceive();

        Console.WriteLine("[CONIO] SUMMARY passed=" + _pass + " failed=" + _fail);
        Console.WriteLine("[CONIO] DONE");
        return (_pass << 16) | _fail;
    }

    // ==================== Output ====================

    private static void TestOutputBasics()
    {
        Console.WriteLine("[CONIO] SECTION output");
        Console.Write("conio-write ");
        Console.Write(42);
        Console.Write(' ');
        Console.Write(1234567890123L);
        Console.Write(' ');
        Console.Write(true);
        Console.Write(" ");
        Console.WriteLine(7);
        Console.WriteLine("conio-write-object " + (object)99);
        Check("write-basics", true);

        // WindowWidth/WindowHeight come from the kernel (80x50 default)
        Check("window-size", Console.WindowWidth == 80 && Console.WindowHeight == 50);

        // Redirection flags are false on the serial console
        Check("redirected-false", !Console.IsInputRedirected && !Console.IsOutputRedirected);
    }

    private static void TestFormattedOutput()
    {
        Console.WriteLine("[CONIO] SECTION format");
        string s1 = string.Format("fmt {0}-{1}", 1, "two");
        Console.WriteLine(s1);
        Console.Write("fmt2 {0} {1} {2}", 3, 4, 5);
        Console.WriteLine();
        Check("string-format", s1 == "fmt 1-two");
    }

    private static void TestEncodingsAndEnvironment()
    {
        Console.WriteLine("[CONIO] SECTION encoding+environment");
        byte[] utf8 = Encoding.UTF8.GetBytes("abc");
        string back = Encoding.UTF8.GetString(utf8);
        Check("utf8-roundtrip", back == "abc" && utf8.Length == 3);
        Check("utf8-nonascii", Encoding.UTF8.GetBytes("\u00E9").Length == 2);
        Check("environment-newline", Environment.NewLine == "\n");
        Check("environment-cwd", Environment.CurrentDirectory == "/");
        Check("output-encoding", Console.OutputEncoding == Encoding.UTF8);
        Console.WriteLine("utf8 console text: caf\u00E9");
    }

    private static void TestColors()
    {
        Console.WriteLine("[CONIO] SECTION colors");
        Console.ForegroundColor = ConsoleColor.DarkRed;
        Console.WriteLine("dark red line");            // ESC[31m
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("bright red line");          // ESC[91m
        Console.ResetColor();                          // ESC[0m
        Console.WriteLine("default color line");
        Check("colors-set", Console.ForegroundColor == ConsoleColor.Gray);
    }

    private static void TestClearAndCursor()
    {
        Console.WriteLine("[CONIO] SECTION clear+cursor");
        Console.Clear();                               // ESC[2J ESC[H
        Console.Write("TOP ");
        Console.SetCursorPosition(10, 5);              // ESC[6;11H
        bool posOk = Console.CursorLeft == 10 && Console.CursorTop == 5;
        Console.WriteLine("cursor-pos-ok=" + posOk);
        Check("cursor-position", posOk);
    }

    // ==================== ReadKey ====================

    private static void TestReadKey()
    {
        Console.WriteLine("[CONIO] SECTION readkey");

        ExpectKey("letter-a", 'a', ConsoleKey.A);
        ExpectKey("letter-Z", 'Z', ConsoleKey.Z);
        ExpectKey("digit-7", '7', ConsoleKey.D7);
        ExpectKey("enter", '\r', ConsoleKey.Enter);
        ExpectKey("escape", '\x1b', ConsoleKey.Escape);
        ExpectKey("tab", '\t', ConsoleKey.Tab);
        ExpectKey("backspace", '\b', ConsoleKey.Backspace);
        ExpectKey("arrow-up", '\0', ConsoleKey.UpArrow);
        ExpectKey("arrow-down", '\0', ConsoleKey.DownArrow);
        ExpectKey("arrow-left", '\0', ConsoleKey.LeftArrow);
        ExpectKey("arrow-right", '\0', ConsoleKey.RightArrow);
        ExpectKey("home", '\0', ConsoleKey.Home);
        ExpectKey("end", '\0', ConsoleKey.End);
        ExpectKey("delete", '\0', ConsoleKey.Delete);
        ExpectKey("pageup", '\0', ConsoleKey.PageUp);
        ExpectKey("pagedown", '\0', ConsoleKey.PageDown);
        for (int i = 0; i < 12; i++)
        {
            ConsoleKey fk = (ConsoleKey)((int)ConsoleKey.F1 + i);
            ExpectKey("f" + (i + 1), '\0', fk);
        }

        // Ctrl+A arrives as 0x01 with the Control modifier
        Console.Write("[CONIO] KEY ctrl-a ");
        var k = Console.ReadKey(true);
        Check("ctrl-a", k.Key == ConsoleKey.A && (k.Modifiers & ConsoleModifiers.Control) != 0);
    }

    private static void ExpectKey(string name, char expectedChar, ConsoleKey expectedKey)
    {
        Console.Write("[CONIO] KEY " + name + " ");
        ConsoleKeyInfo k = Console.ReadKey(true);
        bool ok = k.Key == expectedKey;
        if (expectedChar != '\0' && k.KeyChar != expectedChar)
            ok = false;
        Check("readkey-" + name, ok);
    }

    // ==================== ReadLine ====================

    private static void TestReadLineEditing()
    {
        Console.WriteLine("[CONIO] SECTION readline-editing");
        // Host sends: "abc\x7Fd\r"  ->  backspace erases 'c'
        Console.Write("[CONIO] LINE READY ");
        string? line = Console.ReadLine();
        Check("readline-backspace", line == "abd");
        Console.WriteLine("line=" + line);
    }

    private static void TestReadLineCancellation()
    {
        Console.WriteLine("[CONIO] SECTION readline-cancelled");
        Console.Write("[CONIO] CANCEL READY ");
        // Documented NeutrinoOS deviation from the BCL: Ctrl+C cancels the
        // line by returning null (korlib sets its LastReadLineCanceled flag;
        // no OperationCanceledException - AOT exception unwinding is not
        // available on the kernel console path). The host also verifies that
        // the shell is still alive after the cancellation.
        string? line = Console.ReadLine();
        Check("readline-ctrl-c", line == null);
    }

    private static void TestHistoryRecall()
    {
        Console.WriteLine("[CONIO] SECTION readline-history");
        // 1) submit a line so it lands in history
        Console.Write("[CONIO] HISTORY READY ");
        string? first = Console.ReadLine();
        Check("history-submit", first == "calc 1+1");

        // 2) host sends UP then Enter: the recalled line comes back
        Console.Write("[CONIO] RECALL READY ");
        string? recalled = Console.ReadLine();
        Check("history-recall", recalled == "calc 1+1");
    }

    private static void TestEof()
    {
        Console.WriteLine("[CONIO] SECTION readline-eof");
        Console.Write("[CONIO] EOF READY ");
        string? line = Console.ReadLine();
        Check("readline-eof", line == null);
    }

    // ==================== Bulk RX ====================

    private const int BulkBytes = 10240;

    private static void TestBulkReceive()
    {
        Console.WriteLine("[CONIO] SECTION bulk-rx");
        Console.WriteLine("[CONIO] BULK READY " + BulkBytes);

        int count = 0;
        while (count < BulkBytes)
        {
            // ReadKey(false) consumes + echoes; the RX ring (1024 bytes)
            // absorbs bursts between reads, so a paced 10 KB stream must
            // not drop characters.
            Console.ReadKey(false);
            count++;
        }

        Console.WriteLine();
        Console.WriteLine("[CONIO] BULK COUNT " + count);
        Check("bulk-rx-10kb", count == BulkBytes);
    }
}
