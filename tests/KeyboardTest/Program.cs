// NeutrinoOS Phase 3 - PS/2 keyboard verification test (keyboard_test.dll)
//
// Runs inside the kernel via the Tier-0 JIT when the "run-keyboard-test"
// marker file is present on the boot volume. Interactive: prints the
// ConsoleKeyInfo (key, character, modifiers) of every key pressed on the
// PS/2 keyboard, verifying the scancode-set-1 decoder against a manual
// keypress matrix. Press Escape to exit.
//
// Suggested manual matrix:
//   - letters, digits, symbols (Shift variants), Space
//   - Backspace / Enter / Tab / Escape
//   - Arrow keys, Home/End, Insert/Delete, Page Up/Down
//   - F1..F12
//   - Shift/Ctrl/Alt modifiers, Caps Lock toggle (LED follows)

namespace KeyboardTest;

/// <summary>Phase 3 PS/2 keyboard interactive inspector.</summary>
public static class TestRunner
{
    /// <summary>
    /// Interactive loop: prints ConsoleKeyInfo for every key press until
    /// Escape is pressed.
    /// </summary>
    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("[KbdTest] Press keys to inspect ConsoleKeyInfo; Escape exits.");

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);

            Console.Write("[KbdTest] Key=");
            Console.Write(key.Key.ToString());
            Console.Write("  Char=");

            char c = key.KeyChar;
            if (c == '\0')
                Console.Write("<none>");
            else if (c < 0x20)
            {
                Console.Write("<0x");
                Console.Write(((int)c).ToString("X2"));
                Console.Write(">");
            }
            else
                Console.Write(c);

            Console.Write("  Mods=");
            Console.WriteLine(key.Modifiers.ToString());

            if (key.Key == ConsoleKey.Escape)
                break;
        }
    }
}
