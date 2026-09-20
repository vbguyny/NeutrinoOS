// NeutrinoOS kernel - PS/2 keyboard driver (Phase 3)
//
// 8042 controller bring-up plus a scancode-set-1 decoder. Decoded keys
// are synthesized into the same ANSI byte sequences a serial terminal
// sends (printable ASCII, ESC[A/B/C/D arrows, 0x7F backspace, 0x0D
// Enter, 0x03/0x04 for Ctrl+C/D, ESC[3~ for Delete, ...) and fed to the
// shared LineDiscipline byte sink. This guarantees that editing,
// history, ReadKey and raw mode behave identically on the VGA and serial
// consoles (documented deviation: the spec's separate ConsoleKeyInfo
// decoder is not needed because the discipline produces the key events).
//
// Key repeat is delegated to the PS/2 controller's hardware typematic
// (the keyboard itself resends make codes; QEMU/VirtualBox emulation
// implements this), so no timer integration is required.
//
// The controller is initialized with translation enabled so the host
// always delivers scancode set 1 regardless of the keyboard's native
// set (matches the QEMU and VirtualBox emulation defaults).

using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// PS/2 keyboard driver (IRQ1 / vector 33). Initialized by the CAL once
/// the scheduler and IDT are up.
/// </summary>
public static unsafe class Ps2Keyboard
{
    private const ushort DataPort = 0x60;
    private const ushort StatusPort = 0x64;

    /// <summary>IDT vector for IRQ1 (legacy mapping 32 + 1, routed by IOAPIC).</summary>
    public const int IrqVector = 33;

    private const int TransferGuard = 100000;

    /// <summary>Whether the keyboard has been initialized.</summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>Byte sink (set by the CAL to the line discipline feed).</summary>
    private static delegate* unmanaged<byte, void> _emit;

    // Modifier / lock state
    private static bool _shift;
    private static bool _ctrl;
    private static bool _capsLock;
    private static bool _numLock;
    private static bool _scrollLock;

    // Sequence state
    private static bool _extended;      // saw 0xE0 prefix
    private static int _e1Remaining;    // Pause (0xE1 .. ..) swallow count
    private static bool _psPending;     // PrintScreen (0xE0 2A E0 37) swallow

    // Scancode -> byte tables (filled at Initialize; kernel code avoids
    // static collection initializers so this is done with assignments).
    private static byte[]? _plain;
    private static byte[]? _shifted;

    /// <summary>
    /// Initializes the 8042 controller, enables IRQ1, and wires the byte
    /// sink. Must be called after the scheduler/IDT are up and only once.
    /// </summary>
    /// <param name="emit">Sink receiving the synthesized ANSI byte stream.</param>
    public static void Initialize(delegate* unmanaged<byte, void> emit)
    {
        if (IsInitialized)
            return;

        _emit = emit;

        _plain = new byte[128];
        _shifted = new byte[128];
        BuildTables();

        // --- 8042 bring-up ---
        Uart16550.Write("[PS2-1]");
        WriteCommand(0xAD);         // disable keyboard
        WriteCommand(0xA7);         // disable aux (mouse) port
        DrainOutput();
        Uart16550.Write("[PS2-2]");

        WriteCommand(0x20);         // read configuration byte
        byte config = ReadData();
        config |= 0x01;                          // IRQ1 enable
        config &= unchecked((byte)~0x10);        // keyboard clock enabled
        config |= 0x40;                          // translation -> scancode set 1
        WriteCommand(0x60);         // write configuration byte
        WriteData(config);
        Uart16550.Write("[PS2-3]");

        WriteCommand(0xAE);         // re-enable keyboard
        Uart16550.Write("[PS2-4]");

        ProtonOS.X64.Arch.RegisterHandler(IrqVector, &IrqHandler);
        Uart16550.Write("[PS2-5]");
        IOAPIC.UnmaskIrq(1);
        Uart16550.Write("[PS2-6]");

        IsInitialized = true;
        UpdateLeds();
        Uart16550.Write("[PS2-7]");
    }

    // ==================== IRQ1 ====================

    private static void IrqHandler(InterruptFrame* frame)
    {
        // One interrupt can cover several queued bytes; drain the buffer.
        int guard = 32;
        while ((CPU.InByte(StatusPort) & 0x01) != 0)
        {
            byte data = CPU.InByte(DataPort);
            ProcessScancode(data);
            if (--guard <= 0)
                break;
        }

        APIC.SendEoi();
    }

    /// <summary>
    /// Processes one scancode-set-1 byte (with 0xE0/0xE1 prefixes) and
    /// synthesizes the corresponding ANSI byte sequence into the sink.
    /// </summary>
    public static void ProcessScancode(byte code)
    {
        // Controller/keyboard command responses: ACK, BAT complete,
        // echo, resend, overrun - not key data.
        if (code == 0xFA || code == 0xAA || code == 0xAB || code == 0xFE ||
            code == 0x00 || code == 0xFF)
            return;

        // Pause key: 0xE1 <2 bytes> (make and break forms both have two
        // bytes after 0xE1); swallowed entirely.
        if (_e1Remaining > 0)
        {
            _e1Remaining--;
            return;
        }
        if (code == 0xE1)
        {
            _e1Remaining = 2;
            return;
        }

        if (code == 0xE0)
        {
            _extended = true;
            return;
        }

        bool release = (code & 0x80) != 0;
        byte sc = (byte)(code & 0x7F);

        if (_extended)
        {
            _extended = false;
            HandleExtended(sc, release);
            return;
        }

        switch (sc)
        {
            case 0x2A:              // left Shift
                _shift = !release;
                return;
            case 0x36:              // right Shift
                _shift = !release;
                return;
            case 0x1D:              // left Ctrl
                _ctrl = !release;
                return;
            case 0x38:              // left Alt (no meta emission in Phase 3)
                return;
            case 0x3A:              // Caps Lock
                if (!release) { _capsLock = !_capsLock; UpdateLeds(); }
                return;
            case 0x45:              // Num Lock
                if (!release) { _numLock = !_numLock; UpdateLeds(); }
                return;
            case 0x46:              // Scroll Lock
                if (!release) { _scrollLock = !_scrollLock; UpdateLeds(); }
                return;
        }

        if (release)
            return;

        // Function keys F1-F10
        if (sc >= 0x3B && sc <= 0x44)
        {
            EmitFunctionKey(sc - 0x3B + 1);
            return;
        }
        if (sc == 0x57) { EnsureEscapeBracket(); EmitAsciiByte((byte)'2'); EmitAsciiByte((byte)'3'); EmitAsciiByte((byte)'~'); return; }   // F11
        if (sc == 0x58) { EnsureEscapeBracket(); EmitAsciiByte((byte)'2'); EmitAsciiByte((byte)'4'); EmitAsciiByte((byte)'~'); return; }   // F12

        byte b = Translate(sc);
        if (b != 0)
            EmitAsciiByte(b);
    }

    private static void HandleExtended(byte sc, bool release)
    {
        // PrintScreen: make = E0 2A E0 37, break = E0 B7 E0 AA.
        if (sc == 0x2A || sc == 0xB7)
        {
            _psPending = true;
            return;
        }
        if (_psPending)
        {
            _psPending = false;
            if (sc == 0x37 || sc == 0xAA)
                return;
        }

        switch (sc)
        {
            case 0x1D:              // right Ctrl
                _ctrl = !release;
                return;
            case 0xB6:              // right Shift release (AT keyboards)
                _shift = false;
                return;
            case 0x38:              // right Alt
                return;
            case 0x1C:              // keypad Enter
                if (!release) EmitAsciiByte(0x0D);
                return;
            case 0x35:              // keypad /
                if (!release) EmitAsciiByte((byte)'/');
                return;
        }

        if (release)
            return;

        switch (sc)
        {
            case 0x48: EmitEscapeBracket((byte)'A'); return;   // Up
            case 0x50: EmitEscapeBracket((byte)'B'); return;   // Down
            case 0x4D: EmitEscapeBracket((byte)'C'); return;   // Right
            case 0x4B: EmitEscapeBracket((byte)'D'); return;   // Left
            case 0x47: EmitEscapeBracket((byte)'H'); return;   // Home
            case 0x4F: EmitEscapeBracket((byte)'F'); return;   // End
            case 0x49: EmitEscapeBracket((byte)'5'); EmitAsciiByte((byte)'~'); return;   // PgUp
            case 0x51: EmitEscapeBracket((byte)'6'); EmitAsciiByte((byte)'~'); return;   // PgDn
            case 0x52: EmitEscapeBracket((byte)'2'); EmitAsciiByte((byte)'~'); return;   // Insert
            case 0x53: EmitEscapeBracket((byte)'3'); EmitAsciiByte((byte)'~'); return;   // Delete
            default: return;
        }
    }

    /// <summary>Maps a make-code to a character byte (modifiers applied).</summary>
    private static byte Translate(byte sc)
    {
        if (_plain == null || _shifted == null || sc >= 128)
            return 0;

        byte b = _plain[sc];
        if (b == 0)
            return 0;

        if (_shift)
        {
            byte shifted = _shifted[sc];
            if (shifted != 0)
                b = shifted;
        }

        // Caps Lock affects letters only (and inverts Shift).
        if (_capsLock && b >= 'a' && b <= 'z')
            b = _shift ? _plain[sc] : (byte)(b - 32);
        else if (_capsLock && b >= 'A' && b <= 'Z')
            b = _shift ? (byte)(b + 32) : b;

        if (_ctrl)
        {
            if (b >= 'a' && b <= 'z')
                b = (byte)(b - 'a' + 1);        // Ctrl+A = 0x01 .. Ctrl+Z = 0x1A
            else if (b >= 'A' && b <= 'Z')
                b = (byte)(b - 'A' + 1);
            else if (b == '[')
                b = 0x1B;
            else if (b == '\\')
                b = 0x1C;
            else if (b == ']')
                b = 0x1D;
            else
                return 0;                       // other Ctrl combos: no output
        }

        return b;
    }

    private static void EmitAsciiByte(byte b)
    {
        if (_emit != null)
            _emit(b);
    }

    private static void EmitEscapeBracket(byte final)
    {
        EnsureEscapeBracket();
        EmitAsciiByte(final);
    }

    private static void EnsureEscapeBracket()
    {
        EmitAsciiByte(0x1B);
        EmitAsciiByte((byte)'[');
    }

    private static void EmitEscapeSequence(byte c)
    {
        EnsureEscapeBracket();
        EmitAsciiByte(c);
    }

    private static void EmitFunctionKey(int number)
    {
        // F1-F4: ESC O P/Q/R/S; F5-F10: ESC [ 15~..21~
        if (number <= 4)
        {
            EmitAsciiByte(0x1B);
            EmitAsciiByte((byte)'O');
            EmitAsciiByte((byte)('P' + (number - 1)));
            return;
        }

        EnsureEscapeBracket();
        int code = (number == 5) ? 15 : 17 + (number - 6);   // F5=15, F6=17, F7=18, F8=19, F9=20, F10=21
        EmitDecimal(code);
        EmitAsciiByte((byte)'~');
    }

    private static void EmitDecimal(int value)
    {
        if (value >= 10)
            EmitDecimal(value / 10);
        EmitAsciiByte((byte)('0' + (value % 10)));
    }

    // ==================== LED control ====================

    private static void UpdateLeds()
    {
        if (!IsInitialized)
            return;

        byte mask = 0;
        if (_scrollLock) mask |= 0x01;
        if (_numLock) mask |= 0x02;
        if (_capsLock) mask |= 0x04;

        WriteData(0xED);            // set LEDs command (ACK ignored by decoder)
        WriteData(mask);
    }

    // ==================== 8042 helpers ====================

    private static bool WaitInputClear()
    {
        for (int i = 0; i < TransferGuard; i++)
        {
            if ((CPU.InByte(StatusPort) & 0x02) == 0)
                return true;
        }
        return false;
    }

    private static bool WaitOutputFull()
    {
        for (int i = 0; i < TransferGuard; i++)
        {
            if ((CPU.InByte(StatusPort) & 0x01) != 0)
                return true;
        }
        return false;
    }

    private static void WriteCommand(byte command)
    {
        if (WaitInputClear())
            CPU.OutByte(StatusPort, command);
    }

    private static void WriteData(byte data)
    {
        if (WaitInputClear())
            CPU.OutByte(DataPort, data);
    }

    private static byte ReadData()
    {
        return WaitOutputFull() ? CPU.InByte(DataPort) : (byte)0;
    }

    private static void DrainOutput()
    {
        int guard = 256;
        while ((CPU.InByte(StatusPort) & 0x01) != 0 && guard-- > 0)
            CPU.InByte(DataPort);
    }

    // ==================== Tables ====================

    private static void BuildTables()
    {
        byte[] p = _plain!;
        byte[] s = _shifted!;

        // Esc and the top row
        p[0x01] = 0x1B;
        p[0x02] = (byte)'1';
        p[0x03] = (byte)'2';
        p[0x04] = (byte)'3';
        p[0x05] = (byte)'4';
        p[0x06] = (byte)'5';
        p[0x07] = (byte)'6';
        p[0x08] = (byte)'7';
        p[0x09] = (byte)'8';
        p[0x0A] = (byte)'9';
        p[0x0B] = (byte)'0';
        p[0x0C] = (byte)'-';
        p[0x0D] = (byte)'=';
        p[0x0E] = 0x7F;                 // Backspace (DEL, serial convention)
        p[0x0F] = (byte)'\t';

        // Letters (upper row)
        p[0x10] = (byte)'q';
        p[0x11] = (byte)'w';
        p[0x12] = (byte)'e';
        p[0x13] = (byte)'r';
        p[0x14] = (byte)'t';
        p[0x15] = (byte)'y';
        p[0x16] = (byte)'u';
        p[0x17] = (byte)'i';
        p[0x18] = (byte)'o';
        p[0x19] = (byte)'p';
        p[0x1A] = (byte)'[';
        p[0x1B] = (byte)']';
        p[0x1C] = 0x0D;                 // Enter

        // Letters (home row)
        p[0x1E] = (byte)'a';
        p[0x1F] = (byte)'s';
        p[0x20] = (byte)'d';
        p[0x21] = (byte)'f';
        p[0x22] = (byte)'g';
        p[0x23] = (byte)'h';
        p[0x24] = (byte)'j';
        p[0x25] = (byte)'k';
        p[0x26] = (byte)'l';
        p[0x27] = (byte)';';
        p[0x28] = (byte)'\'';
        p[0x29] = (byte)'`';
        p[0x2B] = (byte)'\\';

        // Letters (bottom row)
        p[0x2C] = (byte)'z';
        p[0x2D] = (byte)'x';
        p[0x2E] = (byte)'c';
        p[0x2F] = (byte)'v';
        p[0x30] = (byte)'b';
        p[0x31] = (byte)'n';
        p[0x32] = (byte)'m';
        p[0x33] = (byte)',';
        p[0x34] = (byte)'.';
        p[0x35] = (byte)'/';
        p[0x37] = (byte)'*';
        p[0x39] = (byte)' ';

        // Keypad (NumLock assumed on - Phase 3 emits digits)
        p[0x47] = (byte)'7';
        p[0x48] = (byte)'8';
        p[0x49] = (byte)'9';
        p[0x4A] = (byte)'-';
        p[0x4B] = (byte)'4';
        p[0x4C] = (byte)'5';
        p[0x4D] = (byte)'6';
        p[0x4E] = (byte)'+';
        p[0x4F] = (byte)'1';
        p[0x50] = (byte)'2';
        p[0x51] = (byte)'3';
        p[0x52] = (byte)'0';
        p[0x53] = (byte)'.';

        // Shifted variants (only keys that differ; letters become upper case)
        s[0x02] = (byte)'!';
        s[0x03] = (byte)'@';
        s[0x04] = (byte)'#';
        s[0x05] = (byte)'$';
        s[0x06] = (byte)'%';
        s[0x07] = (byte)'^';
        s[0x08] = (byte)'&';
        s[0x09] = (byte)'*';
        s[0x0A] = (byte)'(';
        s[0x0B] = (byte)')';
        s[0x0C] = (byte)'_';
        s[0x0D] = (byte)'+';

        s[0x10] = (byte)'Q';
        s[0x11] = (byte)'W';
        s[0x12] = (byte)'E';
        s[0x13] = (byte)'R';
        s[0x14] = (byte)'T';
        s[0x15] = (byte)'Y';
        s[0x16] = (byte)'U';
        s[0x17] = (byte)'I';
        s[0x18] = (byte)'O';
        s[0x19] = (byte)'P';
        s[0x1A] = (byte)'{';
        s[0x1B] = (byte)'}';

        s[0x1E] = (byte)'A';
        s[0x1F] = (byte)'S';
        s[0x20] = (byte)'D';
        s[0x21] = (byte)'F';
        s[0x22] = (byte)'G';
        s[0x23] = (byte)'H';
        s[0x24] = (byte)'J';
        s[0x25] = (byte)'K';
        s[0x26] = (byte)'L';
        s[0x27] = (byte)':';
        s[0x28] = (byte)'"';
        s[0x29] = (byte)'~';
        s[0x2B] = (byte)'|';

        s[0x2C] = (byte)'Z';
        s[0x2D] = (byte)'X';
        s[0x2E] = (byte)'C';
        s[0x2F] = (byte)'V';
        s[0x30] = (byte)'B';
        s[0x31] = (byte)'N';
        s[0x32] = (byte)'M';
        s[0x33] = (byte)'<';
        s[0x34] = (byte)'>';
        s[0x35] = (byte)'?';
    }
}
