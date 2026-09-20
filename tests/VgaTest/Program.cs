// NeutrinoOS Phase 3 - VGA text console acceptance test (vga_test.dll)
//
// Runs inside the kernel via the Tier-0 JIT when the "run-vga-test"
// marker file is present on the boot volume. Exercises the VGA console
// through the public System.Console surface (korlib), i.e. the same API
// user applications use:
//   - Console.Write/WriteLine with colored output (ForegroundColor)
//   - Console.Clear()
//   - Console.SetCursorPosition + cursor position read-back
//   - Extended CP437 characters (box drawing)
//
// The interactive editing behaviors (backspace, arrow-key history,
// Ctrl+C) share the line discipline with the serial console and are
// covered by the automated acceptance runner (wsl-vga-test.py) plus the
// instructions printed at the end of this test.

namespace VgaTest;

/// <summary>Phase 3 VGA console acceptance test entry point.</summary>
public static class TestRunner
{
    private static int _passed;
    private static int _failed;

    /// <summary>
    /// Runs all VGA console checks. Returns (passed &lt;&lt; 16) | failed
    /// so the kernel can report both counters.
    /// </summary>
    public static int RunAllTests()
    {
        _passed = 0;
        _failed = 0;

        Console.WriteLine();
        Console.WriteLine("[VgaTest] VGA console acceptance test start");

        RunCheck("colored output", TestColoredOutput);
        RunCheck("Console.Clear", TestClear);
        RunCheck("SetCursorPosition read-back", TestCursorPosition);
        RunCheck("CP437 extended characters", TestExtendedCharacters);
        RunCheck("scroll + wrap", TestScroll);

        Console.WriteLine("[VgaTest] interactive checks (manual):");
        Console.WriteLine("[VgaTest]   - type 'ab', press Backspace once, type 'z',");
        Console.WriteLine("[VgaTest]     press Enter: the shell must run 'az'.");
        Console.WriteLine("[VgaTest]   - press Up: the previous line must be recalled.");
        Console.WriteLine("[VgaTest]   - press Ctrl+C: '^C' must be echoed and the");
        Console.WriteLine("[VgaTest]     line discarded.");
        Console.WriteLine("[VgaTest] VGA console acceptance test complete");

        return (_passed << 16) | (_failed & 0xFFFF);
    }

    private static void RunCheck(string name, Func<bool> check)
    {
        bool ok;
        try
        {
            ok = check();
        }
        catch (Exception)
        {
            ok = false;
        }
        Console.Write("[VgaTest] ");
        Console.Write(ok ? "PASS " : "FAIL ");
        Console.WriteLine(name);
        if (ok)
            _passed++;
        else
            _failed++;
    }

    private static bool TestColoredOutput()
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("[red] ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("[green] ");
        Console.ResetColor();
        Console.WriteLine("[default]");
        return true;
    }

    private static bool TestClear()
    {
        Console.Clear();
        bool atHome = Console.CursorLeft == 0 && Console.CursorTop == 0;
        Console.WriteLine("[VgaTest] after Clear: cursor at origin");
        return atHome;
    }

    private static bool TestCursorPosition()
    {
        Console.SetCursorPosition(10, 5);
        bool left = Console.CursorLeft == 10;
        bool top = Console.CursorTop == 5;
        Console.Write("(10,5)");
        Console.SetCursorPosition(0, 6);
        return left && top;
    }

    private static bool TestExtendedCharacters()
    {
        // Box-drawing characters map to CP437 glyphs on the VGA console
        // (single-line and double-line frames).
        Console.WriteLine("\u250C\u2500\u2500\u2500\u2510");
        Console.WriteLine("\u2502 x \u2502");
        Console.WriteLine("\u2514\u2500\u2500\u2500\u2518");
        Console.WriteLine("\u2554\u2550\u2550\u2550\u2557");
        Console.WriteLine("\u255A\u2550\u2550\u2550\u255D");
        return true;
    }

    private static bool TestScroll()
    {
        // Force enough lines to make the VGA console scroll at least twice
        // (80x25 has 25 rows; the test log above already uses a few).
        for (int i = 0; i < 30; i++)
        {
            Console.Write("[VgaTest] scroll line ");
            Console.WriteLine(i);
        }
        return true;
    }
}
