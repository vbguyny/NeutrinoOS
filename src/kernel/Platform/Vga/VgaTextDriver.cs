// NeutrinoOS kernel - VGA text-mode hardware driver (Phase 3)
//
// Low-level driver for the standard VGA text framebuffer at physical
// address 0xB8000. The kernel identity-maps physical memory 0-4GB
// (VirtualMemory.Initialize), so the buffer and the CRTC I/O ports can
// be accessed directly without any additional VMM plumbing.
//
// Responsibilities:
//   - Character/attribute cell access (byte pair per cell)
//   - Hardware cursor via CRTC registers 0x0E/0x0F
//   - Scrolling (memmove-style row shift + blank bottom row)
//   - 80x50 mode setup via CRTC/sequencer programming (8x8 font that the
//     firmware VGA BIOS already loaded into plane 2 - no font embedding)
//   - Attribute-controller blink enable/disable (for bright backgrounds)
//
// Text semantics: bytes 0x20-0xFF are written verbatim as CP437 code
// points (the character generator is a CP437 ROM in text mode), so the
// box-drawing codes (0xC4, 0xB3, 0xDA, ...) render directly. This is a
// deliberate byte-oriented contract (documented in docs/PHASE3-DESIGN.md).
//
// Memory model: single VGA head, static state (one text console).

using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// Low-level driver for the standard VGA text-mode framebuffer.
/// </summary>
public static unsafe class VgaTextDriver
{
    /// <summary>Physical address of the VGA text framebuffer.</summary>
    public const uint TextBufferPhysicalAddress = 0xB8000;

    private const ushort CrtcIndexPort = 0x3D4;
    private const ushort CrtcDataPort = 0x3D5;
    private const ushort InputStatus1Port = 0x3DA;
    private const ushort AttributeIndexPort = 0x3C0;
    private const ushort AttributeReadPort = 0x3C1;
    private const ushort SequencerIndexPort = 0x3C4;
    private const ushort SequencerDataPort = 0x3C5;
    private const ushort GraphicsIndexPort = 0x3CE;
    private const ushort GraphicsDataPort = 0x3CF;
    private const ushort DacWriteIndexPort = 0x3C8;
    private const ushort DacDataPort = 0x3C9;

    /// <summary>Default attribute: light grey on black (CP437 "standard").</summary>
    public const byte DefaultAttribute = 0x07;

    /// <summary>Maximum columns the buffer supports (all modes are 80 wide).</summary>
    public const int MaxColumns = 80;

    /// <summary>Maximum rows the buffer supports (80x50 mode).</summary>
    public const int MaxRows = 50;

    private static int _columns = 80;
    private static int _rows = 25;
    private static int _cursorX;
    private static int _cursorY;
    private static byte _attribute = DefaultAttribute;
    private static bool _initialized;
    private static bool _mode80x50;
    private static bool _blinkEnabled = true;

    /// <summary>Raw text framebuffer pointer (identity-mapped physical 0xB8000).</summary>
    private static byte* Buffer => (byte*)TextBufferPhysicalAddress;

    /// <summary>Current column count (80 in both supported modes).</summary>
    public static int Columns => _columns;

    /// <summary>Current row count (25 or 50).</summary>
    public static int Rows => _rows;

    /// <summary>Current cursor column (0-based).</summary>
    public static int CursorX => _cursorX;

    /// <summary>Current cursor row (0-based).</summary>
    public static int CursorY => _cursorY;

    /// <summary>Current default attribute byte used for writes/erase.</summary>
    public static byte Attribute => _attribute;

    /// <summary>Whether the driver has been initialized.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Whether the 80x50 text mode (8x8 font) is active.</summary>
    public static bool IsMode80x50 => _mode80x50;

    // ==================== Initialization ====================

    /// <summary>
    /// Initializes the text console. When <paramref name="mode80x50"/> is
    /// true the CRTC/sequencer are programmed for a 50-row, 8x8-font
    /// display; otherwise a standard 80x25 text mode is set up. The
    /// screen is cleared and the cursor homed in both cases.
    /// </summary>
    /// <remarks>
    /// The firmware (UEFI GOP) typically leaves the adapter in a VBE
    /// linear graphics mode, so a full text-mode register set is
    /// programmed here rather than relying on the firmware's state.
    /// </remarks>
    public static void Initialize(bool mode80x50)
    {
        Uart16550.Write("[VGA-a]");
        ProgramTextMode();
        ProgramDacPalette();
        Uart16550.Write("[VGA-b]");
        if (mode80x50)
            ProgramMode80x50Deltas();
        else
            _rows = 25;

        _columns = 80;
        _attribute = DefaultAttribute;
        _blinkEnabled = true;
        LoadFonts();
        Clear();
        Uart16550.Write("[VGA-c]");
        SetCursorPositionInternal(0, 0);
        Uart16550.Write("[VGA-d]");

        _initialized = true;
    }

    /// <summary>
    /// Programs the canonical VGA 80x25 16-color text mode (mode 3):
    /// disables the Bochs/QEMU VBE extensions, then sets MISC output,
    /// sequencer, CRTC, graphics controller and attribute controller.
    /// </summary>
    private static void ProgramTextMode()
    {
        // Disable any active VBE mode (Bochs VBE extensions at 0x1CE/0x1CF;
        // VBE_DISPI_INDEX_ENABLE = 4). Harmless no-op where the interface
        // is absent; required on QEMU/VirtualBox where the firmware GOP
        // may have a linear framebuffer mode active.
        CPU.OutWord(0x1CE, 4);
        CPU.OutWord(0x1CF, 0);

        // Miscellaneous output: 28MHz clock, color mode, RAM enabled.
        CPU.OutByte(0x3C2, 0x67);

        // Sequencer: sync reset, 8-dot clock, all planes mapped, plain
        // character map, extended memory + odd/even mode, end reset.
        SequencerWrite(0x00, 0x01);
        SequencerWrite(0x01, 0x00);  // 9-dot character clock (classic VGA BIOS mode 3 -> 720x400)
        SequencerWrite(0x02, 0x03);
        SequencerWrite(0x03, 0x00);
        SequencerWrite(0x04, 0x02);
        SequencerWrite(0x00, 0x03);

        // CRTC: unlock registers 0-7, then the canonical 80x25 table.
        CrtcWrite(0x11, 0x0E);
        CrtcWrite(0x00, 0x5F);  // horizontal total
        CrtcWrite(0x01, 0x4F);  // horizontal display end
        CrtcWrite(0x02, 0x50);  // start horizontal blanking
        CrtcWrite(0x03, 0x82);  // end horizontal blanking
        CrtcWrite(0x04, 0x55);  // start horizontal retrace
        CrtcWrite(0x05, 0x81);  // end horizontal retrace
        CrtcWrite(0x06, 0xBF);  // vertical total
        CrtcWrite(0x07, 0x1F);  // overflow
        CrtcWrite(0x08, 0x00);  // preset row scan
        CrtcWrite(0x09, 0x4F);  // maximum scan line: 15 => 16-line chars
        CrtcWrite(0x0A, 0x0D);  // cursor start
        CrtcWrite(0x0B, 0x0E);  // cursor end
        CrtcWrite(0x0C, 0x00);  // start address high
        CrtcWrite(0x0D, 0x00);  // start address low
        CrtcWrite(0x0E, 0x00);  // cursor location high
        CrtcWrite(0x0F, 0x00);  // cursor location low
        CrtcWrite(0x10, 0x9C);  // vertical retrace start
        CrtcWrite(0x11, 0x8E);  // vertical retrace end (write protect on)
        CrtcWrite(0x12, 0x8F);  // vertical display end (399)
        CrtcWrite(0x13, 0x28);  // offset (80 logical columns)
        CrtcWrite(0x14, 0x1F);  // underline location
        CrtcWrite(0x15, 0x96);  // start vertical blanking
        CrtcWrite(0x16, 0xB9);  // end vertical blanking
        CrtcWrite(0x17, 0xA3);  // CRTC mode control
        CrtcWrite(0x18, 0xFF);  // line compare

        // Graphics controller: text mode with odd/even, B8000 map.
        GraphicsWrite(0x00, 0x00);
        GraphicsWrite(0x01, 0x00);
        GraphicsWrite(0x02, 0x00);
        GraphicsWrite(0x03, 0x00);
        GraphicsWrite(0x04, 0x00);
        GraphicsWrite(0x05, 0x10);  // odd/even mode
        GraphicsWrite(0x06, 0x0E);  // memory map: text (B8000)
        GraphicsWrite(0x07, 0x00);
        GraphicsWrite(0x08, 0xFF);

        // Attribute controller: reset flip-flop, blink mode, standard
        // 16-color text palette, then re-enable video output.
        // NOTE: port 0x3C0 takes an INDEX write followed by a DATA write
        // (the internal flip-flop toggles each write). Palette entries
        // must be written as explicit index/data pairs - writing bare
        // values would desynchronize the flip-flop and corrupt the
        // palette (e.g. background color 0).
        CPU.InByte(InputStatus1Port);           // reset flip-flop to index state
        CPU.OutByte(AttributeIndexPort, 0x10);  // mode register
        CPU.OutByte(AttributeIndexPort, 0x0C);  // blink enabled, text mode palette
        for (byte i = 0; i < 16; i++)
        {
            CPU.OutByte(AttributeIndexPort, i);                  // palette index
            CPU.OutByte(AttributeIndexPort, StandardPalette(i)); // palette data (6-bit)
        }
        CPU.OutByte(AttributeIndexPort, 0x11);  // overscan register
        CPU.OutByte(AttributeIndexPort, 0x00);
        CPU.OutByte(AttributeIndexPort, 0x12);  // color plane enable: planes 0-3
        CPU.OutByte(AttributeIndexPort, 0x0F);  // (reset value is 0 -> all colors black;
                                                //  the VGA BIOS always programs this)
        CPU.OutByte(AttributeIndexPort, 0x13);  // horizontal pixel panning
        CPU.OutByte(AttributeIndexPort, 0x08);
        CPU.OutByte(AttributeIndexPort, 0x20);  // enable video
    }

    /// <summary>
    /// Loads the CP437 fonts into VGA plane 2. Under OVMF (no legacy
    /// option-ROM execution / no CSM) nothing populates the font plane,
    /// so the kernel must load the fonts itself; otherwise the adapter
    /// renders blank glyphs.
    ///
    /// The font bitmaps are embedded in the kernel (see VgaFontData,
    /// generated from the standard VGA BIOS character generator) and
    /// written with the classic INT 10h AH=11h sequence: the map mask
    /// selects plane 2 and odd/even address translation is off while the
    /// font bytes are written through the A0000 window.
    /// </summary>
    private static void LoadFonts()
    {
        Uart16550.Write("[VGA-F]");
        SequencerWrite(0x00, 0x01);  // synchronous reset
        SequencerWrite(0x02, 0x04);  // map mask: plane 2 only
        SequencerWrite(0x04, 0x06);  // extended memory, odd/even disabled
        GraphicsWrite(0x05, 0x00);   // write mode 0, odd/even off
        GraphicsWrite(0x06, 0x00);   // memory map: A0000 window

        byte* fontArea = (byte*)0xA0000;
        // Font layout: characters occupy 32 bytes each in the plane-2
        // window - scanline i of character ch lives at window byte
        // 32*ch + i (the same layout the VGA BIOS uses when loading
        // fonts via INT 10h AH=11h). The window is dword-addressed by
        // the controller: window byte K ends up at vram word K, whose
        // third byte is the plane-2 (font) byte.
        for (int part = 0; part < 8; part++)
        {
            string chunk = VgaFontData.Font16Part(part);
            int baseCh = part * 32;
            for (int c = 0; c < 32; c++)
            {
                for (int i = 0; i < 16; i++)
                    fontArea[32 * (baseCh + c) + i] = (byte)chunk[c * 16 + i];
            }
        }
        // The plane-2 offset selected by SR3 map-A differs between
        // interpretations, so place the 8x8 font at all candidate bases
        // in the window (0x2000/0x4000/0x8000 dwords).
        for (int part = 0; part < 4; part++)
        {
            string chunk = VgaFontData.Font8Part(part);
            int baseCh = part * 64;
            for (int c = 0; c < 64; c++)
            {
                for (int i = 0; i < 8; i++)
                {
                    byte v = (byte)chunk[c * 8 + i];
                    fontArea[0x2000 + 32 * (baseCh + c) + i] = v;
                    fontArea[0x4000 + 32 * (baseCh + c) + i] = v;
                    fontArea[0x8000 + 32 * (baseCh + c) + i] = v;
                }
            }
        }

        SequencerWrite(0x00, 0x03);  // end synchronous reset
        // Restore the text-mode state.
        SequencerWrite(0x02, 0x03);  // planes 0+1
        SequencerWrite(0x04, 0x02);  // extended memory, odd/even enabled
        GraphicsWrite(0x05, 0x10);   // odd/even mode
        GraphicsWrite(0x06, 0x0E);   // memory map: B8000
    }

    /// <summary>The standard VGA text-mode palette (attribute palette entries).</summary>
    private static byte StandardPalette(int index)
        => (byte)(index & 0x0F);

    /// <summary>
    /// Programs the first 16 DAC entries with the standard VGA/EGA colors.
    /// The firmware may leave a custom palette (e.g. from a GOP graphics
    /// mode) which would render text in arbitrary colors.
    /// </summary>
    private static void ProgramDacPalette()
    {
        CPU.OutByte(DacWriteIndexPort, 0x00);   // start at DAC entry 0
        DacEntry(0x00, 0x00, 0x00);  // 0 black
        DacEntry(0x00, 0x00, 0x2A);  // 1 blue
        DacEntry(0x00, 0x2A, 0x00);  // 2 green
        DacEntry(0x00, 0x2A, 0x2A);  // 3 cyan
        DacEntry(0x2A, 0x00, 0x00);  // 4 red
        DacEntry(0x2A, 0x00, 0x2A);  // 5 magenta
        DacEntry(0x2A, 0x15, 0x00);  // 6 brown
        DacEntry(0x2A, 0x2A, 0x2A);  // 7 light grey
        DacEntry(0x15, 0x15, 0x15);  // 8 dark grey
        DacEntry(0x15, 0x15, 0x3F);  // 9 bright blue
        DacEntry(0x15, 0x3F, 0x15);  // A bright green
        DacEntry(0x15, 0x3F, 0x3F);  // B bright cyan
        DacEntry(0x3F, 0x15, 0x15);  // C bright red
        DacEntry(0x3F, 0x15, 0x3F);  // D bright magenta
        DacEntry(0x3F, 0x3F, 0x15);  // E yellow
        DacEntry(0x3F, 0x3F, 0x3F);  // F white
    }

    private static void DacEntry(byte r, byte g, byte b)
    {
        CPU.OutByte(DacDataPort, r);
        CPU.OutByte(DacDataPort, g);
        CPU.OutByte(DacDataPort, b);
    }

    /// <summary>
    /// Converts the just-programmed 80x25 mode into 80x50 by switching
    /// to the 8x8 character font. Only three CRTC registers and the
    /// sequencer character-map select differ from the 80x25 table. The
    /// 8x8 font is loaded by <see cref="LoadFonts"/> at plane-2 offset
    /// 0x8000 (map A selector 2), so no font data is embedded here.
    /// </summary>
    private static void ProgramMode80x50Deltas()
    {
        // Character map select: use the 8x8 font for map A. LoadFonts
        // places the 8x8 font at the candidate plane-2 bases used by the
        // various controller interpretations of the map-A select value 2.
        SequencerWrite(0x03, 0x02);

        CrtcWrite(0x09, 0x47);  // maximum scan line: 7 => 8-line chars
        CrtcWrite(0x0A, 0x06);  // cursor start
        CrtcWrite(0x0B, 0x07);  // cursor end

        _rows = 50;
        _mode80x50 = true;
    }

    // ==================== Cell access ====================

    /// <summary>Writes a character with the current attribute at a cell.</summary>
    public static void PutChar(int x, int y, byte ch, byte attribute)
    {
        if ((uint)x >= (uint)_columns || (uint)y >= (uint)_rows)
            return;
        int offset = (y * _columns + x) * 2;
        byte* p = Buffer + offset;
        p[0] = ch;
        p[1] = attribute;
    }

    /// <summary>Reads the character byte of a cell (0 if out of range).</summary>
    public static byte GetChar(int x, int y)
    {
        if ((uint)x >= (uint)_columns || (uint)y >= (uint)_rows)
            return 0;
        return Buffer[(y * _columns + x) * 2];
    }

    /// <summary>Reads the attribute byte of a cell (0 if out of range).</summary>
    public static byte GetAttribute(int x, int y)
    {
        if ((uint)x >= (uint)_columns || (uint)y >= (uint)_rows)
            return 0;
        return Buffer[(y * _columns + x) * 2 + 1];
    }

    // ==================== Screen operations ====================

    /// <summary>Clears the whole screen with the current attribute and homes the cursor.</summary>
    public static void Clear()
    {
        BlankRange(0, _columns * _rows);
        MoveTo(0, 0);
    }

    /// <summary>Erases from the cursor to the end of the screen.</summary>
    public static void ClearToEndOfScreen()
    {
        int start = _cursorY * _columns + _cursorX;
        BlankRange(start, _columns * _rows - start);
    }

    /// <summary>Erases from the cursor to the end of the current line.</summary>
    public static void ClearToEndOfLine()
    {
        int start = _cursorY * _columns + _cursorX;
        BlankRange(start, _columns - _cursorX);
    }

    /// <summary>Erases from the start of the screen to the cursor (inclusive).</summary>
    public static void ClearToStartOfScreen()
    {
        int end = _cursorY * _columns + _cursorX;
        BlankRange(0, end + 1);
    }

    /// <summary>Erases from the start of the line to the cursor (inclusive).</summary>
    public static void ClearToStartOfLine()
    {
        BlankRange(_cursorY * _columns, _cursorX + 1);
    }

    private static void BlankRange(int startCell, int count)
    {
        byte attr = _attribute;
        byte* p = Buffer;
        for (int i = startCell; i < startCell + count; i++)
        {
            p[i * 2] = 0x20;
            p[i * 2 + 1] = attr;
        }
    }

    /// <summary>Scrolls the screen up one row (bottom row blanked).</summary>
    public static void ScrollUp()
    {
        int cells = _columns * _rows;
        int rowCells = _columns;
        byte* p = Buffer;

        // Shift rows 1.._rows-1 up by one row (word copies: char+attr).
        for (int i = 0; i < cells - rowCells; i++)
        {
            p[i * 2] = p[(i + rowCells) * 2];
            p[i * 2 + 1] = p[(i + rowCells) * 2 + 1];
        }

        // Blank the last row.
        byte attr = _attribute;
        for (int i = cells - rowCells; i < cells; i++)
        {
            p[i * 2] = 0x20;
            p[i * 2 + 1] = attr;
        }
    }

    // ==================== Cursor ====================

    /// <summary>Moves the cursor (clamped to the screen) and updates hardware.</summary>
    public static void MoveTo(int x, int y)
    {
        SetCursorPositionInternal(x, y);
    }

    /// <summary>Sets the cursor relative to the current position (clamped).</summary>
    public static void MoveRelative(int dx, int dy)
    {
        SetCursorPositionInternal(_cursorX + dx, _cursorY + dy);
    }

    private static void SetCursorPositionInternal(int x, int y)
    {
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x >= _columns) x = _columns - 1;
        if (y >= _rows) y = _rows - 1;
        _cursorX = x;
        _cursorY = y;
        UpdateHardwareCursor();
    }

    /// <summary>Writes the cursor position to the CRTC (registers 0x0E/0x0F).</summary>
    public static void UpdateHardwareCursor()
    {
        ushort pos = (ushort)(_cursorY * _columns + _cursorX);
        CrtcWrite(0x0E, (byte)(pos >> 8));
        CrtcWrite(0x0F, (byte)(pos & 0xFF));
    }

    /// <summary>Shows the hardware cursor (restores the 80x25-style underline).</summary>
    public static void ShowCursor()
    {
        CrtcWrite(0x0A, _mode80x50 ? (byte)0x06 : (byte)0x0E);
        CrtcWrite(0x0B, _mode80x50 ? (byte)0x07 : (byte)0x0F);
    }

    /// <summary>Hides the hardware cursor.</summary>
    public static void HideCursor()
    {
        CrtcWrite(0x0A, 0x20);
    }

    // ==================== Raw character output ====================
    //
    // The byte-oriented path used by the line-discipline echo: no ANSI
    // interpretation, just CR/LF/BS/printable handling. Safe to call from
    // interrupt context (framebuffer memory writes only; the CRTC cursor
    // update is two port writes).

    /// <summary>
    /// Writes one raw byte at the cursor: 0x0D = CR, 0x0A = LF,
    /// 0x08 = backspace, everything else is a printable CP437 cell.
    /// Wraps at the right edge and scrolls at the bottom.
    /// </summary>
    public static void WriteRawByte(byte b) => WriteRawByteCore(b, true);

    /// <summary>
    /// Like <see cref="WriteRawByte"/> but skips the per-character
    /// hardware cursor update (the CRTC cursor is still updated on line
    /// feeds and scrolls). Used by the early boot-log mirror: the mirror
    /// can emit hundreds of thousands of bytes, and two CRTC port writes
    /// per character are prohibitively slow on hypervisors that trap
    /// every port access (e.g. VirtualBox under NEM).
    /// </summary>
    public static void WriteRawMirrorByte(byte b) => WriteRawByteCore(b, false);

    private static void WriteRawByteCore(byte b, bool updateCursor)
    {
        switch (b)
        {
            case 0x0D:
                if (updateCursor)
                {
                    SetCursorPositionInternal(0, _cursorY);
                }
                else
                {
                    _cursorX = 0;
                }
                return;
            case 0x0A:
                AdvanceLine();
                return;
            case 0x08:
                if (_cursorX > 0)
                {
                    if (updateCursor)
                    {
                        SetCursorPositionInternal(_cursorX - 1, _cursorY);
                    }
                    else
                    {
                        _cursorX--;
                    }
                }
                return;
            default:
                PutChar(_cursorX, _cursorY, b, _attribute);
                _cursorX++;
                if (_cursorX >= _columns)
                {
                    _cursorX = 0;
                    AdvanceLine();
                }
                else
                {
                    if (updateCursor)
                        UpdateHardwareCursor();
                }
                return;
        }
    }

    private static void AdvanceLine()
    {
        _cursorX = 0;
        if (_cursorY + 1 >= _rows)
        {
            ScrollUp();
            _cursorY = _rows - 1;
        }
        else
        {
            _cursorY++;
        }
        UpdateHardwareCursor();
    }

    // ==================== Attributes and colors ====================

    /// <summary>Sets the default attribute used for writes and erase.</summary>
    public static void SetAttribute(byte attribute) => _attribute = attribute;

    /// <summary>Resets the default attribute to light grey on black.</summary>
    public static void ResetAttribute() => _attribute = DefaultAttribute;

    /// <summary>
    /// Composes a text attribute byte from console colors (0-15 each).
    /// Foreground uses bits 0-3 (bit 3 = bright). Background uses bits
    /// 4-6; a background of 8-15 disables blink so bit 7 can act as the
    /// bright-background bit instead (classic DOS "blink off" behavior).
    /// </summary>
    public static byte ComposeAttribute(int foreground, int background)
    {
        int fg = foreground < 0 ? 7 : foreground & 0x0F;
        int bg = background < 0 ? 0 : background & 0x0F;

        if (bg > 7)
        {
            SetBlinkEnabled(false);
            return (byte)(fg | ((bg & 0x07) << 4) | 0x80);
        }

        return (byte)(fg | (bg << 4));
    }

    /// <summary>
    /// Enables or disables the attribute-controller blink mode (bit 3 of
    /// the Attribute Controller Mode register, index 0x10). The
    /// read-modify-write sequence preserves the current palette address
    /// and the flip-flop state.
    /// </summary>
    public static void SetBlinkEnabled(bool enabled)
    {
        if (_blinkEnabled == enabled)
            return;

        // Reading 0x3DA resets the attribute flip-flop so 0x3C0 can be
        // read as the current index.
        CPU.InByte(InputStatus1Port);
        byte savedIndex = CPU.InByte(AttributeIndexPort);

        CPU.OutByte(AttributeIndexPort, 0x10);          // select mode register
        byte mode = CPU.InByte(AttributeReadPort);
        if (enabled)
            mode |= 0x08;
        else
            mode &= unchecked((byte)~0x08);
        CPU.OutByte(AttributeIndexPort, mode);          // write mode data
        CPU.OutByte(AttributeIndexPort, savedIndex);    // restore index

        _blinkEnabled = enabled;
    }

    /// <summary>Whether attribute-controller blink is currently enabled.</summary>
    public static bool IsBlinkEnabled => _blinkEnabled;

    // ==================== Port helpers ====================

    private static void CrtcWrite(byte index, byte value)
    {
        CPU.OutByte(CrtcIndexPort, index);
        CPU.OutByte(CrtcDataPort, value);
    }

    private static void SequencerWrite(byte index, byte value)
    {
        CPU.OutByte(SequencerIndexPort, index);
        CPU.OutByte(SequencerDataPort, value);
    }

    private static void GraphicsWrite(byte index, byte value)
    {
        CPU.OutByte(GraphicsIndexPort, index);
        CPU.OutByte(GraphicsDataPort, value);
    }
}
