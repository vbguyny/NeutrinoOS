// NeutrinoOS kernel - serial console line discipline
//
// Phase 2 input engine. Bytes arrive from the UART RX interrupt handler
// (LineDiscipline.Feed) and are:
//   - canonical mode (default): echoed, line-edited (backspace, Ctrl+U),
//     terminated by Enter (CR/LF), Ctrl+C cancels the line, Ctrl+D signals
//     EOF on an empty line, arrow keys browse up to 32 history entries
//     with full line redraw, and ANSI escape sequences (arrows, Home, End,
//     Delete, PgUp, PgDn, F1-F12, SS3 forms) are decoded into
//     System.ConsoleKeyInfo events.
//   - raw mode: bytes are passed through without echo or editing.
//
// Completed lines/EOF/cancel are queued for ReadLine; decoded keys are
// queued for ReadKey (consumed while a ReadKey call is active, so a
// following ReadLine is not polluted by keys the application read itself).

using System;
using ProtonOS.Arch;

namespace ProtonOS.Platform;

/// <summary>
/// Line discipline for the serial console: echo, editing, history and
/// ANSI escape parsing. Single console (ttyS0); all state is static and
/// accessed from the boot thread and the UART IRQ handler.
/// </summary>
public static unsafe class LineDiscipline
{
    /// <summary>Maximum length of an edited line (in characters).</summary>
    public const int LineCapacity = 256;

    /// <summary>Maximum number of pending completed lines.</summary>
    private const int LineQueueCapacity = 8;

    /// <summary>Maximum number of history entries.</summary>
    public const int HistoryCapacity = 32;

    /// <summary>Maximum number of queued key events.</summary>
    private const int KeyQueueCapacity = 16384;

    // Completed line queue (bytes + lengths + types).
    // NOTE: fixed primitive buffers instead of managed arrays - compiled
    // kernel code must avoid array type tokens (no LdTokenHelpers).
    private struct LineQueueStore
    {
        public fixed byte Data[LineQueueCapacity * LineCapacity];
        public fixed byte Lengths[LineQueueCapacity];
        public fixed byte Types[LineQueueCapacity];
    }

    private static LineQueueStore _lineQueue;
    private static int _lineHead;
    private static int _lineTail;

    // Key event queue (decoded ints; ConsoleKeyInfo is materialized on read)
    private struct KeyQueueStore
    {
        public fixed int KeyChar[KeyQueueCapacity];
        public fixed int KeyCode[KeyQueueCapacity];
        public fixed int Mods[KeyQueueCapacity];
    }

    private static KeyQueueStore _keyQueue;
    private static int _keyHead;
    private static int _keyTail;

    // Line being edited
    private static readonly char[] _edit = new char[LineCapacity];
    private static int _editLength;

    // History (oldest first; data kept as bytes to stay allocation-free).
    private struct HistoryStore
    {
        public fixed byte Data[HistoryCapacity * LineCapacity];
        public fixed byte Lengths[HistoryCapacity];
    }

    private static HistoryStore _historyStore;
    private static int _historyCount;
    private static int _historyPos = -1;                    // -1 = not browsing
    private static readonly char[] _historySaved = new char[LineCapacity];
    private static int _historySavedLength;

    // Modes
    private static bool _rawMode;
    private static bool _treatCtrlCAsInput;
    private static bool _keyReadActive;
    private static bool _keyReadEcho;

    // Phase 5: idle hook (called from PollTimeouts in thread context while
    // the shell waits for input; used by JobManager to run background jobs)
    // and deferred tab completion (completion must not run in the UART ISR -
    // it reads the FAT volume - so TAB sets a flag processed here).
    private static delegate* unmanaged<void> _idleHook;
    private static delegate* unmanaged<char*, int, int> _tabCompleter;
    private static bool _tabPending;
    private static ulong _tabTick;

    /// <summary>
    /// Phase 5: hook called from the blocking read loops (thread context)
    /// while console input is pending. The shell installs its background
    /// job pump here. Keep it short-running when no work is queued.
    /// </summary>
    public static delegate* unmanaged<void> IdleHook
    {
        get => _idleHook;
        set => _idleHook = value;
    }

    /// <summary>
    /// Phase 5: installs the tab-completion callback. It receives the
    /// current edit buffer (chars, not NUL-terminated) and its length, and
    /// returns the new length after completion, -1 for no change, or -2
    /// when it printed a candidate list and the caller should redraw the
    /// prompt + line. Deferred: TAB only sets a flag; the callback runs in
    /// thread context from PollTimeouts (see file header).
    /// </summary>
    public static delegate* unmanaged<char*, int, int> TabCompleter
    {
        get => _tabCompleter;
        set => _tabCompleter = value;
    }

    /// <summary>
    /// Phase 5: echoes one completion candidate line (chars + CRLF) from
    /// thread context (used by the tab completer's candidate listing).
    /// </summary>
    public static void EchoCompletionLine(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            EchoAscii(c < 256 ? (byte)c : (byte)'?');
        }
        EchoAscii(0x0D);
        EchoAscii(0x0A);
    }

    // ANSI escape parser state
    // 0 = normal, 1 = saw ESC, 2 = inside CSI, 3 = saw ESC O (SS3)
    private static int _escState;
    private static int _escParam1;
    private static int _escParam2;
    private static bool _escInParam2;
    private static ulong _escTick;          // tick when the escape started

    /// <summary>
    /// Processes deferred work from the blocking read loops: tab
    /// completion (not safe in the UART ISR), the idle hook (background
    /// jobs) and escape-parser timeouts. A lone ESC becomes the Escape
    /// key after 20 ms; incomplete CSI/SS3 sequences are discarded after
    /// 200 ms (they only occur on line noise). Called from the blocking
    /// read loops, which wake on timer ticks via CPU.Halt().
    /// </summary>
    public static void PollTimeouts()
    {
        ProcessTabCompletion();

        if (_idleHook != null && !_tabPending)
            _idleHook();

        if (_escState == 0)
            return;

        ulong now = ProtonOS.Arch.APIC.TickCount;
        ulong elapsed = now - _escTick;

        if (_escState == 1 && elapsed >= 50)
        {
            _escState = 0;
            DeliverKey('\x1b', ConsoleKey.Escape, ConsoleModifiers.None);
        }
        else if (_escState >= 2 && elapsed >= 200)
        {
            _escState = 0;      // drop the partial sequence
        }
    }

    /// <summary>Whether a decoded key press is waiting.</summary>
    public static bool KeyAvailable => _keyHead != _keyTail;

    /// <summary>Whether the discipline is in raw mode.</summary>
    public static bool IsRawMode => _rawMode;

    /// <summary>Switches between canonical (false) and raw (true) modes.</summary>
    public static void SetRawMode(bool rawMode)
    {
        _rawMode = rawMode;
        _escState = 0;
    }

    /// <summary>Whether Ctrl+C is delivered as a key instead of cancelling.</summary>
    public static void SetTreatControlCAsInput(bool asInput)
        => _treatCtrlCAsInput = asInput;

    /// <summary>Marks that a ReadKey is active (keys go to the key queue).</summary>
    public static void BeginKeyRead(bool echo)
    {
        _keyReadActive = true;
        _keyReadEcho = echo;
    }

    /// <summary>Ends the ReadKey active window.</summary>
    public static void EndKeyRead()
    {
        _keyReadActive = false;
    }

    // ==================== Byte feed (UART IRQ) ====================

    /// <summary>
    /// Feeds one received byte into the discipline. Called from the UART
    /// RX interrupt handler; must not block.
    /// </summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    public static void Feed(byte b)
    {
        FeedCore(b);
    }

    /// <summary>
    /// Managed entry point for feeding one byte (used by the PS/2
    /// keyboard IRQ path and other in-kernel input producers; the UART
    /// path uses the <see cref="Feed"/> function pointer instead).
    /// </summary>
    public static void FeedByte(byte b)
    {
        FeedCore(b);
    }

    private static void FeedCore(byte b)
    {
        if (_rawMode)
        {
            FeedRaw(b);
            return;
        }

        // ANSI escape sequence assembly
        switch (_escState)
        {
            case 1:     // after ESC
                if (b == (byte)'[')
                {
                    _escState = 2;
                    _escParam1 = 0;
                    _escParam2 = 0;
                    _escInParam2 = false;
                    return;
                }
                if (b == (byte)'O')
                {
                    _escState = 3;
                    return;
                }
                // Lone ESC: deliver as the Escape key, then reprocess b
                DeliverKey('\x1b', ConsoleKey.Escape, ConsoleModifiers.None);
                _escState = 0;
                FeedCore(b);
                return;

            case 2:     // CSI: params then final byte
                if (b >= (byte)'0' && b <= (byte)'9')
                {
                    if (_escInParam2)
                        _escParam2 = _escParam2 * 10 + (b - (byte)'0');
                    else
                        _escParam1 = _escParam1 * 10 + (b - (byte)'0');
                    return;
                }
                if (b == (byte)';')
                {
                    _escInParam2 = true;
                    _escParam2 = 0;
                    return;
                }
                if (b >= 0x40 && b <= 0x7E)
                {
                    _escState = 0;
                    HandleCsi(b, _escParam1, _escInParam2 ? _escParam2 : -1);
                    return;
                }
                // Malformed: drop sequence
                _escState = 0;
                return;

            case 3:     // SS3 (ESC O x)
                _escState = 0;
                switch ((char)b)
                {
                    case 'P': DeliverKey('\0', ConsoleKey.F1, ConsoleModifiers.None); return;
                    case 'Q': DeliverKey('\0', ConsoleKey.F2, ConsoleModifiers.None); return;
                    case 'R': DeliverKey('\0', ConsoleKey.F3, ConsoleModifiers.None); return;
                    case 'S': DeliverKey('\0', ConsoleKey.F4, ConsoleModifiers.None); return;
                    case 'H': DeliverKey('\0', ConsoleKey.Home, ConsoleModifiers.None); return;
                    case 'F': DeliverKey('\0', ConsoleKey.End, ConsoleModifiers.None); return;
                    default: return;
                }
        }

        // Normal byte processing
        if (b == 0x1B)
        {
            _escState = 1;
            _escTick = ProtonOS.Arch.APIC.TickCount;
            return;
        }

        // When a ReadKey is pending, all bytes become key events (except
        // Enter, which also completes a line if one is being edited).
        if (_keyReadActive)
        {
            HandleKeyConsumerByte(b);
            return;
        }

        HandleCanonicalByte(b);
    }

    private static void FeedRaw(byte b)
    {
        // Raw mode: no echo, no editing. CR/LF complete the line; all other
        // bytes (including control codes) are appended verbatim.
        if (b == 0x0D || b == 0x0A)
        {
            CompleteLine(LineType.Line);
            return;
        }

        if (_keyReadActive)
        {
            DeliverKey((char)b, KeyFromChar(b), ModifiersFromByte(b));
            return;
        }

        if (_editLength < LineCapacity)
            _edit[_editLength++] = (char)b;
    }

    // ==================== Canonical-mode byte handling ====================

    private static void HandleCanonicalByte(byte b)
    {
        switch (b)
        {
            case 0x0D:      // CR (Enter)
            case 0x0A:      // LF (Enter)
                EchoAscii(0x0D);
                EchoAscii(0x0A);
                CompleteLine(LineType.Line);
                return;

            case 0x7F:      // DEL / backspace
            case 0x08:      // BS
                if (_editLength > 0)
                {
                    _editLength--;
                    _historyPos = -1;
                    EchoAscii(0x08);
                    EchoAscii((byte)' ');
                    EchoAscii(0x08);
                }
                return;

            case 0x03:      // Ctrl+C
                if (_treatCtrlCAsInput)
                {
                    EchoString("^C");
                    DeliverKey((char)0x03, ConsoleKey.C, ConsoleModifiers.Control);
                }
                else
                {
                    EchoString("^C");
                    EchoAscii(0x0D);
                    EchoAscii(0x0A);
                    _editLength = 0;
                    _historyPos = -1;
                    CompleteLine(LineType.Cancelled);
                }
                return;

            case 0x04:      // Ctrl+D
                if (_editLength == 0)
                {
                    EchoAscii(0x0D);
                    EchoAscii(0x0A);
                    CompleteLine(LineType.Eof);
                }
                else
                {
                    EchoAscii(0x0D);
                    EchoAscii(0x0A);
                    CompleteLine(LineType.Line);
                }
                return;

            case 0x15:      // Ctrl+U - clear the line
                RedrawBegin();
                _editLength = 0;
                _historyPos = -1;
                return;

            case 0x09:      // Tab - Phase 5: request deferred completion
                if (_tabCompleter != null && !_keyReadActive)
                {
                    _tabPending = true;
                    _tabTick = ProtonOS.Arch.APIC.TickCount;
                }
                else
                {
                    DeliverKey('\t', ConsoleKey.Tab, ConsoleModifiers.None);
                }
                return;
        }

        if (b < 0x20)
        {
            // Other control codes (Ctrl+A etc.): deliver as keys, no append
            char letter = (char)('A' + (b - 1));
            if (b >= 0x01 && b <= 0x1A)
                DeliverKey((char)b, KeyFromChar((byte)letter), ConsoleModifiers.Control);
            return;
        }

        // Printable (ASCII and passthrough high bytes)
        if (_editLength < LineCapacity)
        {
            _edit[_editLength++] = (char)b;
            _historyPos = -1;
            EchoAscii(b);
        }
    }

    private static void HandleKeyConsumerByte(byte b)
    {
        switch (b)
        {
            case 0x0D:
            case 0x0A:
                DeliverKey('\r', ConsoleKey.Enter, ConsoleModifiers.None);
                return;

            case 0x7F:
            case 0x08:
                DeliverKey('\b', ConsoleKey.Backspace, ConsoleModifiers.None);
                return;

            case 0x09:
                DeliverKey('\t', ConsoleKey.Tab, ConsoleModifiers.None);
                return;

            case 0x1B:
                // Handled by the escape parser before this point
                DeliverKey('\x1b', ConsoleKey.Escape, ConsoleModifiers.None);
                return;

            case 0x03:
                DeliverKey((char)0x03, ConsoleKey.C, ConsoleModifiers.Control);
                return;
        }

        if (b < 0x20)
        {
            char letter = (char)('A' + (b - 1));
            if (b >= 0x01 && b <= 0x1A)
            {
                DeliverKey((char)b, KeyFromChar((byte)letter), ConsoleModifiers.Control);
            }
            return;
        }

        DeliverKey((char)b, KeyFromChar(b), ConsoleModifiers.None);
    }

    // ==================== CSI / key decoding ====================

    private static void HandleCsi(byte final, int p1, int p2)
    {
        ConsoleModifiers mods = ModifiersFromCsiParam(p2);

        switch ((char)final)
        {
            case 'A': HandleArrow(ConsoleKey.UpArrow, mods); return;
            case 'B': HandleArrow(ConsoleKey.DownArrow, mods); return;
            case 'C': DeliverKey('\0', ConsoleKey.RightArrow, mods); return;
            case 'D': DeliverKey('\0', ConsoleKey.LeftArrow, mods); return;
            case 'H': DeliverKey('\0', ConsoleKey.Home, mods); return;
            case 'F': DeliverKey('\0', ConsoleKey.End, mods); return;

            case '~':
                switch (p1)
                {
                    case 1: DeliverKey('\0', ConsoleKey.Home, mods); return;
                    case 2: DeliverKey('\0', ConsoleKey.Insert, mods); return;
                    case 3: DeliverKey('\0', ConsoleKey.Delete, mods); return;
                    case 4: DeliverKey('\0', ConsoleKey.End, mods); return;
                    case 5: DeliverKey('\0', ConsoleKey.PageUp, mods); return;
                    case 6: DeliverKey('\0', ConsoleKey.PageDown, mods); return;
                    case 15: DeliverKey('\0', ConsoleKey.F5, mods); return;
                    case 17: DeliverKey('\0', ConsoleKey.F6, mods); return;
                    case 18: DeliverKey('\0', ConsoleKey.F7, mods); return;
                    case 19: DeliverKey('\0', ConsoleKey.F8, mods); return;
                    case 20: DeliverKey('\0', ConsoleKey.F9, mods); return;
                    case 21: DeliverKey('\0', ConsoleKey.F10, mods); return;
                    case 23: DeliverKey('\0', ConsoleKey.F11, mods); return;
                    case 24: DeliverKey('\0', ConsoleKey.F12, mods); return;
                }
                return;
        }
    }

    private static ConsoleModifiers ModifiersFromCsiParam(int p2)
    {
        // xterm modifier encoding: 2=Shift, 3=Alt, 5=Ctrl (xterm uses
        // 1+bitmask; accept both common forms)
        switch (p2)
        {
            case 2: case 4: return ConsoleModifiers.Shift;
            case 3: return ConsoleModifiers.Alt;
            case 5: return ConsoleModifiers.Control;
            case 6: case 8: return ConsoleModifiers.Shift | ConsoleModifiers.Alt;
            case 7: return ConsoleModifiers.Alt | ConsoleModifiers.Control;
            default: return ConsoleModifiers.None;
        }
    }

    private static void HandleArrow(ConsoleKey key, ConsoleModifiers mods)
    {
        // While a key consumer is active, arrows are plain keys.
        if (_keyReadActive || _rawMode)
        {
            DeliverKey('\0', key, mods);
            return;
        }

        // Line editor: arrows browse history
        if (key == ConsoleKey.UpArrow)
            HistoryPrev();
        else
            HistoryNext();
    }

    private static ConsoleKey KeyFromChar(byte b)
    {
        if (b >= (byte)'a' && b <= (byte)'z')
            return (ConsoleKey)('A' + (b - 'a'));
        if (b >= (byte)'A' && b <= (byte)'Z')
            return (ConsoleKey)b;
        if (b >= (byte)'0' && b <= (byte)'9')
            return (ConsoleKey)b;
        if (b == (byte)' ')
            return ConsoleKey.Spacebar;
        return (ConsoleKey)0;
    }

    private static ConsoleModifiers ModifiersFromByte(byte b)
    {
        if (b >= 0x01 && b <= 0x1A)
            return ConsoleModifiers.Control;
        return ConsoleModifiers.None;
    }

    // ==================== Key queue ====================

    private static void DeliverKey(char ch, ConsoleKey key, ConsoleModifiers mods)
    {
        // Echo the character for ReadKey(false) (intercept == false)
        if (_keyReadActive && _keyReadEcho && ch != '\0')
            EchoAscii(ch < 256 ? (byte)ch : (byte)'?');

        int next = (_keyTail + 1) & (KeyQueueCapacity - 1);
        if (next == _keyHead)
            return;     // queue full: drop

        fixed (KeyQueueStore* q = &_keyQueue)
        {
            q->KeyChar[_keyTail] = ch;
            q->KeyCode[_keyTail] = (int)key;
            q->Mods[_keyTail] = (int)mods;
        }
        _keyTail = next;
    }

    /// <summary>Attempts to dequeue a decoded key event.</summary>
    public static bool TryDequeueKey(out ConsoleKeyInfo key)
    {
        if (_keyHead == _keyTail)
        {
            key = default;
            return false;
        }

        char ch;
        int code, mods;
        fixed (KeyQueueStore* q = &_keyQueue)
        {
            ch = (char)q->KeyChar[_keyHead];
            code = q->KeyCode[_keyHead];
            mods = q->Mods[_keyHead];
        }
        _keyHead = (_keyHead + 1) & (KeyQueueCapacity - 1);

        key = new ConsoleKeyInfo(ch, (ConsoleKey)code,
            (mods & (int)ConsoleModifiers.Shift) != 0,
            (mods & (int)ConsoleModifiers.Alt) != 0,
            (mods & (int)ConsoleModifiers.Control) != 0);
        return true;
    }

    // ==================== Line completion / queues ====================

    private enum LineType : byte
    {
        Line = 0,
        Eof = 1,
        Cancelled = 2
    }

    private static void CompleteLine(LineType type)
    {
        // Drop any completion that has not run yet: the line is going to
        // the consumer now, and a late completion would otherwise fire on
        // the next (empty) edit buffer.
        _tabPending = false;

        int next = (_lineTail + 1) & (LineQueueCapacity - 1);
        if (next == _lineHead)
            return;     // queue full: drop

        int slot = _lineTail;
        fixed (LineQueueStore* q = &_lineQueue)
        {
            int len = 0;
            if (type == LineType.Line)
            {
                len = _editLength;
                for (int i = 0; i < len; i++)
                {
                    char c = _edit[i];
                    q->Data[slot * LineCapacity + i] = c < 256 ? (byte)c : (byte)'?';
                }
                if (len > 0)
                    PushHistory(slot, len);
            }
            q->Lengths[slot] = (byte)len;
            q->Types[slot] = (byte)type;
        }
        _lineTail = next;

        _editLength = 0;
        _historyPos = -1;
        _historySavedLength = 0;
    }

    private static bool TryDequeueLineInto(Span<char> destination, out int length, out byte type)
    {
        if (_lineHead == _lineTail)
        {
            length = 0;
            type = 0;
            return false;
        }

        int slot = _lineHead;
        fixed (LineQueueStore* q = &_lineQueue)
        {
            type = q->Types[slot];
            int len = q->Lengths[slot];
            if (len > destination.Length)
                len = destination.Length;
            for (int i = 0; i < len; i++)
                destination[i] = (char)q->Data[slot * LineCapacity + i];
            length = len;
        }
        _lineHead = (_lineHead + 1) & (LineQueueCapacity - 1);
        return true;
    }

    /// <summary>
    /// Blocks until a complete line is available. Returns the number of
    /// characters copied, -1 for EOF, -2 for cancellation.
    /// </summary>
    public static int ReadLine(Span<char> destination)
    {
        while (true)
        {
            if (TryDequeueLineInto(destination, out int length, out byte type))
            {
                if (type == (byte)LineType.Eof)
                    return -1;
                if (type == (byte)LineType.Cancelled)
                    return -2;
                return length;
            }

            CPU.Halt();     // wake on RX/timer interrupt
            PollTimeouts();
        }
    }

    // ==================== History ====================

    /// <summary>Pushes the just-completed edit buffer into history.</summary>
    private static void PushHistory(int lineSlot, int lineLength)
    {
        fixed (LineQueueStore* q = &_lineQueue)
        fixed (HistoryStore* h = &_historyStore)
        {
            // Skip consecutive duplicates
            if (_historyCount > 0)
            {
                int last = _historyCount - 1;
                int lastLen = h->Lengths[last];
                if (lastLen == lineLength)
                {
                    bool same = true;
                    for (int i = 0; i < lineLength; i++)
                    {
                        if (h->Data[last * LineCapacity + i] != q->Data[lineSlot * LineCapacity + i])
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same)
                        return;
                }
            }

            if (_historyCount >= HistoryCapacity)
            {
                // Shift entries down (oldest first ordering)
                for (int i = 1; i < HistoryCapacity; i++)
                {
                    int dst = i - 1;
                    h->Lengths[dst] = h->Lengths[i];
                    for (int j = 0; j < h->Lengths[i]; j++)
                        h->Data[dst * LineCapacity + j] = h->Data[i * LineCapacity + j];
                }
                _historyCount = HistoryCapacity - 1;
            }

            int target = _historyCount;
            h->Lengths[target] = (byte)lineLength;
            for (int i = 0; i < lineLength; i++)
                h->Data[target * LineCapacity + i] = q->Data[lineSlot * LineCapacity + i];
            _historyCount++;
        }
    }

    private static void HistoryPrev()
    {
        if (_historyCount == 0)
            return;

        if (_historyPos == -1)
        {
            // Save the line being edited so Down can restore it
            _historySavedLength = _editLength;
            for (int i = 0; i < _editLength; i++)
                _historySaved[i] = _edit[i];
            _historyPos = _historyCount;
        }

        if (_historyPos == 0)
            return;

        _historyPos--;
        RecallHistory(_historyPos);
    }

    private static void HistoryNext()
    {
        if (_historyPos == -1)
            return;

        _historyPos++;
        if (_historyPos >= _historyCount)
        {
            // Back to the line that was being edited
            _historyPos = -1;
            RedrawBegin();
            for (int i = 0; i < _historySavedLength; i++)
                EchoAscii((byte)_historySaved[i]);
            _editLength = _historySavedLength;
            for (int i = 0; i < _historySavedLength; i++)
                _edit[i] = _historySaved[i];
            return;
        }

        RecallHistory(_historyPos);
    }

    private static void RecallHistory(int index)
    {
        fixed (HistoryStore* h = &_historyStore)
        {
            int len = h->Lengths[index];
            RedrawBegin();
            for (int i = 0; i < len; i++)
            {
                byte b = h->Data[index * LineCapacity + i];
                EchoAscii(b);
                _edit[i] = (char)b;
            }
            _editLength = len;
        }
    }

    // ==================== Phase 5 history API ====================

    /// <summary>Number of history entries currently stored (oldest first).</summary>
    public static int GetHistoryCount() => _historyCount;

    /// <summary>
    /// Copies history entry <paramref name="index"/> (0 = oldest) into
    /// <paramref name="destination"/>; returns the entry length.
    /// </summary>
    public static int GetHistoryEntry(int index, char[] destination)
    {
        if (index < 0 || index >= _historyCount)
            return 0;
        fixed (HistoryStore* h = &_historyStore)
        {
            int len = h->Lengths[index];
            if (len > destination.Length)
                len = destination.Length;
            for (int i = 0; i < len; i++)
                destination[i] = (char)h->Data[index * LineCapacity + i];
            return len;
        }
    }

    /// <summary>
    /// Appends a line to the history (used to preload the persistent
    /// history file at shell startup; the edit path uses PushHistory).
    /// Consecutive duplicates are skipped; the oldest entry is dropped
    /// when the 32-entry store is full.
    /// </summary>
    public static void AddHistoryEntry(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        fixed (HistoryStore* h = &_historyStore)
        {
            if (_historyCount > 0)
            {
                int last = _historyCount - 1;
                int lastLen = h->Lengths[last];
                bool same = lastLen == line.Length;
                if (same)
                {
                    for (int i = 0; i < lastLen; i++)
                    {
                        if (h->Data[last * LineCapacity + i] != (byte)line[i])
                        {
                            same = false;
                            break;
                        }
                    }
                }
                if (same)
                    return;
            }

            if (_historyCount >= HistoryCapacity)
            {
                for (int i = 1; i < HistoryCapacity; i++)
                {
                    for (int k = 0; k < LineCapacity; k++)
                        h->Data[(i - 1) * LineCapacity + k] = h->Data[i * LineCapacity + k];
                    h->Lengths[i - 1] = h->Lengths[i];
                }
                _historyCount = HistoryCapacity - 1;
            }

            int slot = _historyCount;
            int len = line.Length > LineCapacity ? LineCapacity : line.Length;
            for (int i = 0; i < len; i++)
                h->Data[slot * LineCapacity + i] = line[i] < 256 ? (byte)line[i] : (byte)'?';
            h->Lengths[slot] = (byte)len;
            _historyCount++;
        }
    }

    /// <summary>
    /// Runs the tab completer (thread context, after a 20 ms quiet period
    /// so a burst of TABs coalesces). The completer edits the line buffer
    /// in place; appended characters are echoed here. Return -2 means the
    /// completer printed a candidate list and the line is redrawn.
    /// </summary>
    private static void ProcessTabCompletion()
    {
        if (!_tabPending)
            return;

        // Debounce: wait until the line has been quiet briefly.
        if (ProtonOS.Arch.APIC.TickCount - _tabTick < 20)
            return;

        _tabPending = false;
        if (_rawMode || _keyReadActive || _tabCompleter == null)
            return;

        int newLen;
        fixed (char* p = _edit)
        {
            newLen = _tabCompleter(p, _editLength);
        }

        if (newLen < 0)
        {
            if (newLen == -2)
            {
                // Candidate list was printed; redraw prompt + current line.
                RedrawBegin();
                for (int i = 0; i < _editLength; i++)
                    EchoAsciiChar(_edit[i]);
            }
            return;
        }

        if (newLen > _editLength)
        {
            for (int i = _editLength; i < newLen; i++)
                EchoAsciiChar(_edit[i]);
            _editLength = newLen;
            _historyPos = -1;
        }
        else if (newLen < _editLength)
        {
            // Defensive: completer shortened the line; redraw it.
            _editLength = newLen;
            RedrawBegin();
            for (int i = 0; i < _editLength; i++)
                EchoAsciiChar(_edit[i]);
        }
    }

    /// <summary>
    /// Erases the current line (prompt + text) and re-prints the prompt.
    /// The caller then echoes the replacement text.
    /// </summary>
    private static void RedrawBegin()
    {
        CalcPromptTailLength(out char[] prompt, out int promptLen);

        int oldLen = promptLen + _editLength;
        EchoAscii(0x0D);
        for (int i = 0; i < oldLen; i++)
            EchoAscii((byte)' ');
        EchoAscii(0x0D);
        for (int i = 0; i < promptLen; i++)
            EchoAsciiChar(prompt[i]);
    }

    private static readonly char[] _promptScratch = new char[64];

    private static void CalcPromptTailLength(out char[] prompt, out int length)
    {
        var device = ConsoleAbstractionLayer.Devices.ActiveInput;
        if (device != null)
        {
            length = device.GetPromptTail(new Span<char>(_promptScratch));
        }
        else
        {
            length = 0;
        }
        prompt = _promptScratch;
        if (length > _promptScratch.Length)
            length = _promptScratch.Length;
    }

    // ==================== Echo helpers ====================
    //
    // Echo runs in the UART RX interrupt handler (Feed -> FeedCore/FeedRaw),
    // so it must never block: the blocking console write path could spin on
    // a full TX ring while the THRE interrupt that would drain it is
    // blocked by the active ISR.  Echo is best-effort - dropped bytes are
    // acceptable under load; the decoded input path is unaffected.
    //
    // Phase 3: the CAL installs an echo sink that mirrors echo bytes to
    // BOTH the serial UART and the VGA text console, so line editing is
    // visible on whichever console the user is typing on. Without a sink
    // echo falls back to the UART only.

    private static delegate* unmanaged<byte, void> _echoSink;

    /// <summary>
    /// Installs the echo byte sink (set by the CAL during console
    /// initialization). The sink must be ISR-safe and non-blocking.
    /// </summary>
    public static void SetEchoSink(delegate* unmanaged<byte, void> sink)
    {
        _echoSink = sink;
    }

    private static void EchoAscii(byte b)
    {
        if (_echoSink != null)
            _echoSink(b);
        else
            Uart16550.TryWriteByte(b);
    }

    private static void EchoAsciiChar(char c)
    {
        EchoAscii(c < 256 ? (byte)c : (byte)'?');
    }

    private static void EchoString(string s)
    {
        for (int i = 0; i < s.Length; i++)
            EchoAsciiChar(s[i]);
    }
}
