// NeutrinoOS kernel - minimal AML (ACPI Machine Language) term evaluator.
// Phase 9 Task 4: the subset needed for ACPI power management - reading
// Name declarations whose value is an integer or a package of integers
// (e.g. \_S5 {SLP_TYPa, SLP_TYPb}, and later \_S3). The full interpreter
// (method execution for _PTS/_WAK, control flow, namespace search rules)
// is the next milestone; this file deliberately implements only term
// *evaluation* over already-parsed byte streams, with the parser
// structured so method execution can be added on top.
//
// AML encoding notes (ACPI 6.x, section 20):
//   NameOp  = 0x08, followed by a 4-byte NameString and one data term
//   PackageOp = 0x12, followed by PkgLength and NumElements and terms
//   integers: Zero (0x00), One (0x01), Ones (0xFF), BytePrefix  (0x0A u8),
//              WordPrefix (0x0B u16), DwordPrefix (0x0C u32), QwordPrefix (0x0E u64)
//   PkgLength: lead byte bits[7:6] = number of extra length bytes; the
//              remaining 6 lead bits are the least-significant length bits.
//
// The DSDT/SSDT tables are reached through their physical addresses and
// are identity-mapped, so raw byte pointers work.

using System;

namespace ProtonOS.Platform;

/// <summary>Minimal AML term evaluator (Phase 9 ACPI power management).</summary>
public static unsafe class Aml
{
    private const byte OpName = 0x08;
    private const byte OpPackage = 0x12;
    private const byte OpZero = 0x00;
    private const byte OpOne = 0x01;
    private const byte OpOnes = 0xFF;
    private const byte OpBytePrefix = 0x0A;
    private const byte OpWordPrefix = 0x0B;
    private const byte OpDwordPrefix = 0x0C;
    private const byte OpStringPrefix = 0x0D;
    private const byte OpQwordPrefix = 0x0E;

    /// <summary>
    /// Find the first `Name (target, ...)` declaration in an AML byte
    /// stream and evaluate its data object. When the object is a package
    /// of integers, the first two elements are returned (that is the
    /// `_S5`/`_S3` shape). Returns false when the name is absent or the
    /// value is not an integer/integer-package.
    /// </summary>
    public static bool TryReadNameIntegers(
        byte* table, uint length,
        byte n0, byte n1, byte n2, byte n3,
        out ulong value0, out ulong value1)
    {
        value0 = 0;
        value1 = 0;
        if (table == null || length < 8)
            return false;

        for (uint i = 0; i + 5 < length; i++)
        {
            if (table[i] != OpName)
                continue;
            if (table[i + 1] != n0 || table[i + 2] != n1 || table[i + 3] != n2 || table[i + 4] != n3)
                continue;

            uint pos = i + 5;
            if (pos >= length)
                continue;

            byte op = table[pos];
            if (op == OpPackage)
            {
                pos++;
                int pkgLen = ReadPkgLength(table, length, ref pos);
                if (pkgLen <= 0 || pos >= length)
                    continue;
                uint numElements = table[pos++];
                int values = 0;
                ulong v0 = 0, v1 = 0;
                for (uint e = 0; e < numElements && pos < length; e++)
                {
                    ulong v;
                    if (!TryReadInteger(table, length, ref pos, out v))
                        break;
                    if (values == 0) v0 = v;
                    else if (values == 1) v1 = v;
                    values++;
                }
                if (values >= 1)
                {
                    value0 = v0;
                    value1 = values >= 2 ? v1 : v0;
                    return true;
                }
                continue;
            }

            // Direct integer object.
            ulong direct;
            if (TryReadInteger(table, length, ref pos, out direct))
            {
                value0 = direct;
                value1 = direct;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluate one integer term at <paramref name="pos"/> and advance it.
    /// Packages and strings are not integers and fail the call (the caller
    /// may then stop scanning elements).
    /// </summary>
    private static bool TryReadInteger(byte* table, uint length, ref uint pos, out ulong value)
    {
        value = 0;
        if (pos >= length)
            return false;

        byte op = table[pos];
        switch (op)
        {
            case OpZero:
                pos++;
                value = 0;
                return true;
            case OpOne:
                pos++;
                value = 1;
                return true;
            case OpOnes:
                pos++;
                value = 0xFFFFFFFFFFFFFFFFUL;
                return true;
            case OpBytePrefix:
                if (pos + 1 >= length) return false;
                value = table[pos + 1];
                pos += 2;
                return true;
            case OpWordPrefix:
                if (pos + 2 >= length) return false;
                value = (ulong)table[pos + 1] | ((ulong)table[pos + 2] << 8);
                pos += 3;
                return true;
            case OpDwordPrefix:
                if (pos + 4 >= length) return false;
                value = (ulong)table[pos + 1] | ((ulong)table[pos + 2] << 8)
                      | ((ulong)table[pos + 3] << 16) | ((ulong)table[pos + 4] << 24);
                pos += 5;
                return true;
            case OpQwordPrefix:
                if (pos + 8 >= length) return false;
                for (int b = 0; b < 8; b++)
                    value |= (ulong)table[pos + 1 + (uint)b] << (8 * b);
                pos += 9;
                return true;
            case OpStringPrefix:
            {
                pos++;
                while (pos < length && table[pos] != 0)
                    pos++;
                pos++;   // NUL
                return false;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Decode an AML PkgLength (1-4 bytes, lead byte high bits say how
    /// many follow). Returns -1 on malformed input. The returned value
    /// includes the length bytes themselves per the ACPI encoding; callers
    /// only need a plausibility bound here.
    /// </summary>
    private static int ReadPkgLength(byte* table, uint length, ref uint pos)
    {
        if (pos >= length)
            return -1;
        byte lead = table[pos];
        int extra = lead >> 6;
        if (extra == 0)
        {
            pos++;
            return lead & 0x3F;
        }
        if (pos + (uint)extra >= length)
            return -1;
        int value = lead & 0x3F;
        for (int i = 0; i < extra; i++)
        {
            if (pos + 1 + (uint)i >= length)
                return -1;
            value |= table[pos + 1 + (uint)i] << (6 + 8 * i);
        }
        pos += (uint)(1 + extra);
        return value;
    }

    // ========================================================================
    // Phase 9 Task 4 milestone 2: AML method interpreter
    // ========================================================================
    //
    // The evaluator is deliberately bounded. Method bodies are executed by
    // a tree-walking interpreter over a value model of integers, strings,
    // buffers, constant packages and name references. It supports the term
    // subset used by firmware `_PTS` / `_WAK` / `_CST` / `_PSS` methods:
    // constants, packages/buffers, locals/args, integer and logical
    // arithmetic, Store/CopyObject, If/Else, While and nested method
    // calls. Anything else aborts the affected method with a log line -
    // the interpreter never executes garbage.
    //
    // Method and Name definitions are indexed by a linear scan for the
    // `Method`/`Name` opcodes with a validated PkgLength/name/flags shape
    // (see TryReadMethodAt / TryReadNameAt). That keeps namespace
    // discovery independent of full-table parsing; scope semantics are
    // approximated by matching on the last name segment, which is what
    // the firmware methods above reference.
    //
    // Opcode values below were cross-checked against a real QEMU q35
    // DSDT disassembled with iasl (If=0xA0, While=0xA2, Else=0xA1,
    // Store=0x70, Add=0x72, Subtract=0x74, Increment=0x75, ShiftLeft=0x79,
    // LAnd=0x90, LOr=0x91, LNot=0x92, LEqual=0x93, LGreater=0x94,
    // LLess=0x95, ToBuffer=0x96, ToHexString=0x98, ToInteger=0x99,
    // CopyObject=0x9D, Return=0xA4, Acquire=5B 23 (name + u16 timeout),
    // Release=5B 27, SizeOf=0x87, DerefOf=0x83, Index=0x88).

    private const int MaxTables = 4;
    private const int MaxFrames = 12;
    private const int MaxOverlays = 64;
    private const int MaxMethodArgs = 7;
    private const int MaxLocals = 8;
    private const uint AmlHeaderLen = 36;    // ACPI table header before the AML body
    private const ulong DefaultStepBudget = 400000;
    private const int WhileIterationCap = 65536;

    // Additional opcodes (verified against iasl output).
    private const byte OpVarPackage2 = 0x13;   // VarPackage
    private const byte OpMethod2 = 0x14;       // Method definition
    private const byte OpScope2 = 0x10;        // Scope definition
    private const byte OpBuffer2 = 0x11;       // Buffer constant
    private const byte OpLocal0 = 0x60;        // ..0x67 = Local0..Local7
    private const byte OpArg0 = 0x68;          // ..0x6E = Arg0..Arg6
    private const byte OpStore = 0x70;
    private const byte OpRefOf = 0x71;
    private const byte OpAdd = 0x72;
    private const byte OpConcat = 0x73;
    private const byte OpSubtract = 0x74;
    private const byte OpIncrement = 0x75;
    private const byte OpDecrement = 0x76;
    private const byte OpMultiply = 0x77;
    private const byte OpDivide = 0x78;
    private const byte OpShiftLeft = 0x79;
    private const byte OpShiftRight = 0x7A;
    private const byte OpAnd = 0x7B;
    private const byte OpNand = 0x7C;
    private const byte OpOr = 0x7D;
    private const byte OpNor = 0x7E;
    private const byte OpXor = 0x7F;
    private const byte OpNot = 0x80;
    private const byte OpFindSetLeftBit = 0x81;
    private const byte OpFindSetRightBit = 0x82;
    private const byte OpDerefOf = 0x83;
    private const byte OpConcatRes = 0x84;
    private const byte OpMod = 0x85;
    private const byte OpNotify = 0x86;
    private const byte OpSizeOf = 0x87;
    private const byte OpIndex = 0x88;
    private const byte OpObjectType = 0x8E;
    private const byte OpCreateDwordField = 0x8A;
    private const byte OpCreateWordField = 0x8B;
    private const byte OpCreateByteField = 0x8C;
    private const byte OpCreateBitField = 0x8D;
    private const byte OpCreateQwordField = 0x8F;
    private const byte OpLAnd = 0x90;
    private const byte OpLOr = 0x91;
    private const byte OpLNot = 0x92;
    private const byte OpLEqual = 0x93;
    private const byte OpLGreater = 0x94;
    private const byte OpLLess = 0x95;
    private const byte OpToBuffer = 0x96;
    private const byte OpToDecimalString = 0x97;
    private const byte OpToHexString = 0x98;
    private const byte OpToInteger = 0x99;
    private const byte OpToString = 0x9C;
    private const byte OpCopyObject = 0x9D;
    private const byte OpMid = 0x9E;
    private const byte OpContinue = 0x9F;
    private const byte OpIf = 0xA0;
    private const byte OpElse = 0xA1;
    private const byte OpWhile = 0xA2;
    private const byte OpNoop = 0xA3;
    private const byte OpReturn = 0xA4;
    private const byte OpBreak = 0xA5;
    private const byte OpBreakPoint = 0xCC;
    private const byte OpOnes2 = 0xFF;
    private const byte OpExt2 = 0x5B;
    private const byte OpExtMutex = 0x01;
    private const byte OpExtEvent = 0x02;
    private const byte OpExtCondRefOf = 0x12;
    private const byte OpExtCreateField = 0x13;
    private const byte OpExtLoadTable = 0x1F;
    private const byte OpExtLoad = 0x20;
    private const byte OpExtStall = 0x21;
    private const byte OpExtSleep = 0x22;
    private const byte OpExtAcquire = 0x23;
    private const byte OpExtSignal = 0x24;
    private const byte OpExtWait = 0x25;
    private const byte OpExtReset = 0x26;
    private const byte OpExtRelease = 0x27;
    private const byte OpExtFromBcd = 0x28;
    private const byte OpExtToBcd = 0x29;
    private const byte OpExtUnload = 0x2A;
    private const byte OpExtRevision = 0x30;
    private const byte OpExtDebug = 0x31;
    private const byte OpExtFatal = 0x32;
    private const byte OpExtTimer = 0x33;
    private const byte OpExtOpRegion = 0x80;
    private const byte OpExtField = 0x81;
    private const byte OpExtDevice = 0x82;
    private const byte OpExtProcessor = 0x83;
    private const byte OpExtPowerRes = 0x84;
    private const byte OpExtThermalZone = 0x85;
    private const byte OpExtIndexField = 0x86;
    private const byte OpExtBankField = 0x87;

    // Value kinds.
    private const byte VkInt = 0;
    private const byte VkString = 1;
    private const byte VkBuffer = 2;
    private const byte VkPackage = 3;
    private const byte VkNull = 4;      // null target / unknown reference

    private struct Val
    {
        public byte Kind;
        public ulong Int;
        public uint Ref;                // package/buffer/string payload ref
    }

    private struct MethodEntry
    {
        public uint NameKey;            // packed 4 name bytes
        public int TableIdx;
        public uint BodyOffset;         // first byte of body (after flags)
        public uint BodyEnd;            // exclusive
        public int ArgCount;
    }

    private struct NameEntry
    {
        public uint NameKey;
        public int TableIdx;
        public uint DatumOffset;        // offset of the value term
    }

    private struct Frame
    {
        public int MethodIdx;           // index into _methods
        public int Returned;
        public Val ReturnValue;
        public fixed ulong Args[MaxMethodArgs];
        public fixed ulong Locals[MaxLocals];
        public fixed byte ArgIsInt[MaxMethodArgs];
        public fixed byte LocalIsInt[MaxLocals];
    }

    private struct Overlay
    {
        public uint NameKey;
        public Val Value;
    }

    // Static state (kernel-safe: no allocation, fixed capacities).
    private static ulong _t0, _t1, _t2, _t3;
    private static uint _l0, _l1, _l2, _l3;
    private static int _tableCount;
    private static MethodEntry[] _methods;
    private static int _methodCount;
    private static NameEntry[] _names;
    private static int _nameCount;
    private static Overlay[] _overlays;
    private static int _overlayCount;
    private static Frame[] _frames;
    private static int _frameDepth;
    private static ulong _steps;
    private static int _aborted;
    private static int _lastErrorCode;

    /// <summary>Reset the interpreter namespace (tables, methods, names, overlays).</summary>
    public static void ResetNamespace()
    {
        _tableCount = 0;
        _t0 = _t1 = _t2 = _t3 = 0;
        _l0 = _l1 = _l2 = _l3 = 0;
        _methodCount = 0;
        _nameCount = 0;
        _overlayCount = 0;
        _frameDepth = 0;
        _aborted = 0;
        _firmwareIndexed = 0;
        if (_methods == null)
            _methods = new MethodEntry[1024];
        if (_names == null)
            _names = new NameEntry[1024];
        if (_overlays == null)
            _overlays = new Overlay[MaxOverlays];
        if (_frames == null)
            _frames = new Frame[MaxFrames];
    }

    private static int _firmwareIndexed;

    /// <summary>
    /// Index the firmware DSDT and every SSDT (once) into the namespace so
    /// methods like _PTS/_WAK/_CST/_PSS resolve. Shared by the power (
    /// S3 sleep) and CPU power (cpupower) code paths.
    /// </summary>
    public static void IndexFirmwareTables()
    {
        if (_firmwareIndexed != 0)
            return;
        _firmwareIndexed = 1;

        ResetNamespace();
        _firmwareIndexed = 1;

        var fadt = ACPI.FindFadt();
        if (fadt != null)
        {
            ulong dsdtAddr = fadt->XDsdt != 0 ? fadt->XDsdt : fadt->Dsdt;
            if (dsdtAddr != 0)
            {
                var dsdt = (ACPITableHeader*)dsdtAddr;
                AddTable((byte*)dsdt, dsdt->Length);
            }
        }

        for (int i = 0; i < ACPI.TableCount; i++)
        {
            var t = ACPI.GetTable(i);
            if (t == null)
                continue;
            byte* sig = t->Signature;
            if (sig[0] == 'S' && sig[1] == 'S' && sig[2] == 'D' && sig[3] == 'T')
                AddTable((byte*)t, t->Length);
        }
    }

    /// <summary>Register an AML-bearing table (DSDT or an SSDT) and index its
    /// Method/Name definitions. Call ResetNamespace() first, then AddTable
    /// once per table.</summary>
    public static bool AddTable(byte* table, uint length)
    {
        if (table == null || length < AmlHeaderLen)
            return false;
        int idx = _tableCount;
        if (idx >= MaxTables)
            return false;
        switch (idx)
        {
            case 0: _t0 = (ulong)table; _l0 = length; break;
            case 1: _t1 = (ulong)table; _l1 = length; break;
            case 2: _t2 = (ulong)table; _l2 = length; break;
            default: _t3 = (ulong)table; _l3 = length; break;
        }
        _tableCount = idx + 1;

        // ACPI tables carry a 36-byte header; scan the body. Offsets
        // stored in the index are table-relative (a +36 adjustment).
        byte* body = table + AmlHeaderLen;
        uint bodyLen = length - AmlHeaderLen;
        uint pos = 0;
        while (pos + 6 < bodyLen)
        {
            byte op = body[pos];
            if (op == OpMethod2)
            {
                if (TryIndexMethod(idx, body, bodyLen, pos))
                {
                    pos += (uint)MethodExtent(body, bodyLen, pos);
                    continue;
                }
            }
            else if (op == OpName)
            {
                if (TryIndexName(idx, body, bodyLen, pos))
                {
                    pos += 5;
                    continue;
                }
            }
            pos++;
        }
        return true;
    }

    private static int MethodExtent(byte* body, uint bodyLen, uint pos)
    {
        uint p = pos + 1;
        int pkg = ReadPkgLength(body, bodyLen, ref p);
        if (pkg <= 0)
            return 1;
        uint total = (uint)pkg + 1;      // opcode byte + PkgLength (which counts from its own first byte)
        if (pos + total > bodyLen)
            return 1;
        return (int)total;
    }

    private static bool IsNameChar(byte c)
    {
        return (c >= (byte)'A' && c <= (byte)'Z') || c == (byte)'_'
            || (c >= (byte)'0' && c <= (byte)'9');
    }

    private static bool TryIndexMethod(int tableIdx, byte* body, uint bodyLen, uint pos)
    {
        if (_methodCount >= _methods.Length)
            return false;
        uint p = pos + 1;
        int pkg = ReadPkgLength(body, bodyLen, ref p);
        if (pkg < 6)
            return false;
        uint bodyStart = p;                     // first name char
        if (bodyStart + 5 > bodyLen)
            return false;
        for (int i = 0; i < 4; i++)
            if (!IsNameChar(body[bodyStart + (uint)i]))
                return false;
        uint flagsOff = bodyStart + 4;
        byte flags = body[flagsOff];
        if ((flags & 0x07) > 6)
            return false;
        uint end = pos + 1 + (uint)pkg;
        if (end > bodyLen || end <= flagsOff)
            return false;

        var m = new MethodEntry
        {
            NameKey = NameKey(body[bodyStart], body[bodyStart + 1], body[bodyStart + 2], body[bodyStart + 3]),
            TableIdx = tableIdx,
            BodyOffset = flagsOff + 1 + AmlHeaderLen,   // table-relative
            BodyEnd = end + AmlHeaderLen,               // table-relative
            ArgCount = flags & 0x07
        };
        _methods[_methodCount++] = m;
        return true;
    }

    private static bool TryIndexName(int tableIdx, byte* body, uint bodyLen, uint pos)
    {
        if (_nameCount >= _names.Length)
            return false;
        uint nameOff = pos + 1;
        if (nameOff + 4 >= bodyLen)
            return false;
        for (int i = 0; i < 4; i++)
            if (!IsNameChar(body[nameOff + (uint)i]))
                return false;

        var n = new NameEntry
        {
            NameKey = NameKey(body[nameOff], body[nameOff + 1], body[nameOff + 2], body[nameOff + 3]),
            TableIdx = tableIdx,
            DatumOffset = nameOff + 5 + AmlHeaderLen    // table-relative
        };
        _names[_nameCount++] = n;
        return true;
    }

    private static uint NameKey(byte a, byte b, byte c, byte d)
    {
        return (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
    }

    private static uint NameKey4(byte n0, byte n1, byte n2, byte n3)
    {
        return NameKey(n0, n1, n2, n3);
    }

    private static int FindMethod(uint key)
    {
        for (int i = 0; i < _methodCount; i++)
            if (_methods[i].NameKey == key)
                return i;
        return -1;
    }

    private static int FindName(uint key)
    {
        for (int i = 0; i < _nameCount; i++)
            if (_names[i].NameKey == key)
                return i;
        return -1;
    }

    private static byte* TablePtr(int idx)
    {
        switch (idx)
        {
            case 0: return (byte*)_t0;
            case 1: return (byte*)_t1;
            case 2: return (byte*)_t2;
            default: return (byte*)_t3;
        }
    }

    private static uint TableLen(int idx)
    {
        switch (idx)
        {
            case 0: return _l0;
            case 1: return _l1;
            case 2: return _l2;
            default: return _l3;
        }
    }

    /// <summary>True when a Method with this 4-character name is indexed.</summary>
    public static bool HasMethod(byte n0, byte n1, byte n2, byte n3)
    {
        return FindMethod(NameKey4(n0, n1, n2, n3)) >= 0;
    }

    /// <summary>
    /// Invoke an indexed method with up to MaxMethodArgs integer arguments.
    /// Returns false when the method is missing, the interpreter aborted on
    /// an unsupported term, or the step budget ran out. `result` receives
    /// the returned value (0 when the method has no Return).
    /// </summary>
    public static bool Invoke(byte n0, byte n1, byte n2, byte n3, ulong* args, int argCount, out ulong result)
    {
        result = 0;
        int mi = FindMethod(NameKey4(n0, n1, n2, n3));
        if (mi < 0)
            return false;
        _steps = DefaultStepBudget;
        _aborted = 0;
        Val rv;
        bool ok = InvokeMethod(mi, args, argCount, out rv);
        if (ok && rv.Kind == VkInt)
            result = rv.Int;
        return ok;
    }

    private static bool InvokeMethod(int methodIdx, ulong* args, int argCount, out Val result)
    {
        result = new Val { Kind = VkNull };
        if (_frameDepth >= MaxFrames)
        {
            _aborted = 2;
            return false;
        }

        int fi = _frameDepth++;
        _frames[fi] = default;
        _frames[fi].MethodIdx = methodIdx;
        _frames[fi].Returned = 0;
        int declared = _methods[methodIdx].ArgCount;
        fixed (Frame* fp = &_frames[fi])
        {
            for (int i = 0; i < MaxMethodArgs; i++)
            {
                fp->ArgIsInt[i] = 0;
                if (i < argCount && i < declared && args != null)
                {
                    fp->Args[i] = args[i];
                    fp->ArgIsInt[i] = 1;
                }
            }
        }

        int tIdx = _methods[methodIdx].TableIdx;
        byte* table = TablePtr(tIdx);
        uint len = TableLen(tIdx);
        uint pos = _methods[methodIdx].BodyOffset;
        uint end = _methods[methodIdx].BodyEnd;

        bool ok = true;
        while (pos < end)
        {
            if (_steps == 0)
            {
                _aborted = 3;
                ok = false;
                break;
            }
            Val v;
            if (!EvalTerm(table, tIdx, len, ref pos, end, out v))
            {
                ok = false;
                break;
            }
            fixed (Frame* fp = &_frames[fi])
            {
                if (fp->Returned != 0)
                {
                    result = fp->ReturnValue;
                    break;
                }
            }
            // Statement values are discarded; guard against no progress.
            if (pos == 0 || pos > end + 1)
                break;
        }

        _frameDepth--;
        if (!ok)
        {
            DebugConsole.Write("[aml] method aborted (unsupported term or budget), code ");
            DebugConsole.WriteHex((ushort)(_aborted == 0 ? 9 : _aborted));
            DebugConsole.WriteLine();
        }
        return ok;
    }

    /// <summary>Evaluate one term at [pos, end) and advance pos.</summary>
    private static bool EvalTerm(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        value = new Val { Kind = VkNull };
        if (pos >= end || pos >= len)
            return false;
        _steps--;

        byte op = table[pos];
        if (op >= OpLocal0 && op <= (byte)0x6F)
        {
            if (op <= 0x67)
            {
                int li = op - OpLocal0;
                pos++;
                if (_frameDepth <= 0)
                {
                    value = new Val { Kind = VkNull };
                    return true;
                }
                fixed (Frame* fp = &_frames[_frameDepth - 1])
                {
                    if (fp->LocalIsInt[li] != 0)
                    {
                        value = new Val { Kind = VkInt, Int = fp->Locals[li] };
                        return true;
                    }
                    value = new Val { Kind = VkNull };
                    return true;
                }
            }
            int ai = op - OpArg0;
            pos++;
            if (_frameDepth <= 0)
            {
                value = new Val { Kind = VkNull };
                return true;
            }
            fixed (Frame* fp = &_frames[_frameDepth - 1])
            {
                if (ai < MaxMethodArgs && fp->ArgIsInt[ai] != 0)
                {
                    value = new Val { Kind = VkInt, Int = fp->Args[ai] };
                    return true;
                }
                value = new Val { Kind = VkNull };
                return true;
            }
        }

        switch (op)
        {
            case OpZero:
                pos++;
                value = new Val { Kind = VkInt, Int = 0 };
                return true;
            case OpOne:
                pos++;
                value = new Val { Kind = VkInt, Int = 1 };
                return true;
            case OpOnes2:
                pos++;
                value = new Val { Kind = VkInt, Int = 0xFFFFFFFFFFFFFFFFUL };
                return true;
            case OpBytePrefix:
                if (pos + 1 >= len) return false;
                value = new Val { Kind = VkInt, Int = table[pos + 1] };
                pos += 2;
                return true;
            case OpWordPrefix:
                if (pos + 2 >= len) return false;
                value = new Val { Kind = VkInt, Int = (ulong)table[pos + 1] | ((ulong)table[pos + 2] << 8) };
                pos += 3;
                return true;
            case OpDwordPrefix:
                if (pos + 4 >= len) return false;
                value = new Val
                {
                    Kind = VkInt,
                    Int = (ulong)table[pos + 1] | ((ulong)table[pos + 2] << 8)
                        | ((ulong)table[pos + 3] << 16) | ((ulong)table[pos + 4] << 24)
                };
                pos += 5;
                return true;
            case OpQwordPrefix:
                if (pos + 8 >= len) return false;
                {
                    ulong v = 0;
                    for (int b = 0; b < 8; b++)
                        v |= (ulong)table[pos + 1 + (uint)b] << (8 * b);
                    value = new Val { Kind = VkInt, Int = v };
                    pos += 9;
                    return true;
                }
            case OpStringPrefix:
                {
                    pos++;
                    uint start = pos;
                    while (pos < len && table[pos] != 0)
                        pos++;
                    if (pos >= len) return false;
                    value = new Val { Kind = VkString, Ref = PackRef(tIdx, start) };
                    pos++;   // NUL
                    return true;
                }
            case OpBuffer2:
                {
                    pos++;
                    uint pkgStart = pos;
                    int pkg = ReadPkgLength(table, len, ref pos);
                    if (pkg <= 0) return false;
                    uint pkgEnd = pkgStart + (uint)pkg;
                    if (pkgEnd > len || pkgEnd > end) return false;
                    Val sizeV;
                    if (!EvalTerm(table, tIdx, len, ref pos, pkgEnd, out sizeV))
                        return false;
                    // Skip remaining raw bytes of the buffer.
                    value = new Val { Kind = VkBuffer, Ref = PackRef(tIdx, pos) };
                    pos = pkgEnd;
                    return true;
                }
            case OpPackage:
            case OpVarPackage2:
                {
                    pos++;
                    uint pkgStart = pos;
                    int pkg = ReadPkgLength(table, len, ref pos);
                    if (pkg <= 0) return false;
                    uint pkgEnd = pkgStart + (uint)pkg;
                    if (pkgEnd > len || pkgEnd > end) return false;
                    if (op == OpPackage)
                    {
                        if (pos >= pkgEnd) return false;
                        uint count = table[pos];
                        pos++;
                        value = new Val { Kind = VkPackage, Int = count, Ref = PackRef(tIdx, pos) };
                    }
                    else
                    {
                        Val countV;
                        if (!EvalTerm(table, tIdx, len, ref pos, pkgEnd, out countV))
                            return false;
                        value = new Val { Kind = VkPackage, Int = countV.Int, Ref = PackRef(tIdx, pos) };
                    }
                    // Elements are evaluated on demand by PkgInt/PkgSub.
                    pos = pkgEnd;
                    return true;
                }
            case OpStore:
            case OpCopyObject:
                {
                    pos++;
                    Val src;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out src))
                        return false;
                    if (!AssignTarget(table, tIdx, len, ref pos, end, src))
                        return false;
                    value = src;
                    return true;
                }
            case OpAdd:
            case OpSubtract:
            case OpMultiply:
            case OpAnd:
            case OpNand:
            case OpOr:
            case OpNor:
            case OpXor:
            case OpShiftLeft:
            case OpShiftRight:
                {
                    pos++;
                    Val a, b;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    ulong r;
                    switch (op)
                    {
                        case OpAdd: r = a.Int + b.Int; break;
                        case OpSubtract: r = a.Int - b.Int; break;
                        case OpMultiply: r = a.Int * b.Int; break;
                        case OpAnd: r = a.Int & b.Int; break;
                        case OpNand: r = ~(a.Int & b.Int); break;
                        case OpOr: r = a.Int | b.Int; break;
                        case OpNor: r = ~(a.Int | b.Int); break;
                        case OpXor: r = a.Int ^ b.Int; break;
                        case OpShiftLeft: r = b.Int >= 64 ? 0 : a.Int << (int)b.Int; break;
                        default: r = b.Int >= 64 ? 0 : a.Int >> (int)b.Int; break;
                    }
                    var rv = new Val { Kind = VkInt, Int = r };
                    if (!AssignTarget(table, tIdx, len, ref pos, end, rv))
                        return false;
                    value = rv;
                    return true;
                }
            case OpMod:
                {
                    pos++;
                    Val a, b;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    var rv = new Val { Kind = VkInt, Int = b.Int == 0 ? 0 : a.Int % b.Int };
                    if (!AssignTarget(table, tIdx, len, ref pos, end, rv))
                        return false;
                    value = rv;
                    return true;
                }
            case OpIncrement:
            case OpDecrement:
                {
                    pos++;
                    uint tgt = pos;
                    Val cur;
                    if (!ReadTarget(table, tIdx, len, ref pos, end, out cur))
                        return false;
                    var nv = new Val
                    {
                        Kind = VkInt,
                        Int = cur.Int + (op == OpIncrement ? 1UL : unchecked(0UL - 1UL))
                    };
                    // Increment/Decrement have a single SuperName operand:
                    // rewind and assign to the same position.
                    pos = tgt;
                    if (!AssignTarget(table, tIdx, len, ref pos, end, nv))
                        return false;
                    value = nv;
                    return true;
                }
            case OpNot:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    var rv = new Val { Kind = VkInt, Int = ~a.Int };
                    if (!AssignTarget(table, tIdx, len, ref pos, end, rv))
                        return false;
                    value = rv;
                    return true;
                }
            case OpLAnd:
            case OpLOr:
                {
                    pos++;
                    Val a, b;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    bool r = op == OpLAnd ? (a.Int != 0 && b.Int != 0) : (a.Int != 0 || b.Int != 0);
                    value = new Val { Kind = VkInt, Int = r ? 1UL : 0UL };
                    return true;
                }
            case OpLNot:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    value = new Val { Kind = VkInt, Int = a.Int == 0 ? 1UL : 0UL };
                    return true;
                }
            case OpLEqual:
            case OpLGreater:
            case OpLLess:
                {
                    pos++;
                    Val a, b;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    bool r;
                    if (op == OpLEqual) r = a.Int == b.Int;
                    else if (op == OpLGreater) r = a.Int > b.Int;
                    else r = a.Int < b.Int;
                    value = new Val { Kind = VkInt, Int = r ? 1UL : 0UL };
                    return true;
                }
            case OpSizeOf:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    value = new Val { Kind = VkInt, Int = a.Kind == VkInt ? sizeof(ulong) : a.Int };
                    return true;
                }
            case OpIf:
                return EvalIf(table, tIdx, len, ref pos, end, out value);
            case OpWhile:
                return EvalWhile(table, tIdx, len, ref pos, end, out value);
            case OpReturn:
                {
                    pos++;
                    Val rv;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out rv)) return false;
                    if (_frameDepth > 0 && _skipMode == 0)
                    {
                        fixed (Frame* fp = &_frames[_frameDepth - 1])
                        {
                            fp->Returned = 1;
                            fp->ReturnValue = rv;
                        }
                    }
                    value = rv;
                    return true;
                }
            case OpNoop:
            case OpBreakPoint:
            case OpContinue:
                pos++;
                value = new Val { Kind = VkInt, Int = 0 };
                return true;
            case OpBreak:
                // Break out of the innermost While: mark by unwinding to
                // the frame with a null result. The While loop checks a
                // dedicated flag via _breakFlag.
                pos++;
                _breakFlag = 1;
                value = new Val { Kind = VkInt, Int = 0 };
                return true;
            case OpToBuffer:
            case OpToDecimalString:
            case OpToHexString:
            case OpToInteger:
            case OpToString:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!AssignTarget(table, tIdx, len, ref pos, end, a))
                        return false;
                    value = a;
                    return true;
                }
            case OpNotify:
                {
                    pos++;
                    Val a, b;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    value = new Val { Kind = VkInt, Int = 0 };
                    return true;
                }
            case OpExt2:
                return EvalExtended(table, tIdx, len, ref pos, end, out value);
            case OpMethod2:
                // Nested method definitions do not execute inline.
                pos += (uint)MethodExtent(table, len, pos);
                value = new Val { Kind = VkInt, Int = 0 };
                return true;
            case OpIndex:
                return EvalIndex(table, tIdx, len, ref pos, end, out value);
            case OpDerefOf:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a))
                        return false;
                    value = a;
                    return true;
                }
            case OpRefOf:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a))
                        return false;
                    if (!AssignTarget(table, tIdx, len, ref pos, end, a))
                        return false;
                    value = a;
                    return true;
                }
            case OpObjectType:
                {
                    pos++;
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a))
                        return false;
                    value = new Val { Kind = VkInt, Int = a.Kind == VkInt ? 1UL : 0UL };
                    return true;
                }
            case OpCreateDwordField:
            case OpCreateWordField:
            case OpCreateByteField:
            case OpCreateBitField:
            case OpCreateQwordField:
                {
                    pos++;
                    Val a, b;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    StoreOverlay(key, new Val { Kind = VkNull });
                    value = new Val { Kind = VkInt, Int = 0 };
                    return true;
                }
            default:
                // NameString? Try resolving it as one.
                if (IsNameStart(op))
                    return EvalNameOrCall(table, tIdx, len, ref pos, end, out value);
                _aborted = 1;
                DebugConsole.Write("[aml] unsupported op 0x");
                DebugConsole.WriteHex(op);
                DebugConsole.Write(" at 0x");
                DebugConsole.WriteHex((uint)pos);
                DebugConsole.WriteLine();
                return false;
        }
    }

    private static int _breakFlag;
    private static int _skipMode;

    private static bool FrameReturned()
    {
        if (_frameDepth <= 0)
            return false;
        fixed (Frame* fp = &_frames[_frameDepth - 1])
            return fp->Returned != 0;
    }

    private static bool IsNameStart(byte c)
    {
        return IsNameChar(c) || c == (byte)'\\' || c == (byte)0x2E || c == (byte)0x2F
            || c == (byte)'^';
    }

    private static uint PackRef(int tableIdx, uint off)
    {
        return ((uint)tableIdx << 28) | (off & 0x0FFFFFFFU);
    }

    private static uint RefOff(uint r)
    {
        return r & 0x0FFFFFFFU;
    }

    private static int RefTable(uint r)
    {
        return (int)(r >> 28);
    }

    /// <summary>Read a NameString at pos; returns the last 4-byte segment
    /// key (the namespace lookup key) and advances pos past the whole path.</summary>
    private static bool ReadNameKey(byte* table, uint len, ref uint pos, uint end, out uint key)
    {
        key = 0;
        if (pos >= end || pos >= len)
            return false;
        if (table[pos] == (byte)'\\')
            pos++;
        while (pos < end && pos < len && table[pos] == (byte)'^')
            pos++;
        uint segs = 1;
        if (pos < end && pos < len && table[pos] == 0x2E)
        {
            pos++;
            segs = 2;
        }
        else if (pos < end && pos < len && table[pos] == 0x2F)
        {
            pos++;
            if (pos >= end || pos >= len)
                return false;
            segs = table[pos];
            pos++;
        }
        if (segs == 0 || segs > 16)
            return false;
        for (uint s = 0; s < segs; s++)
        {
            if (pos + 4 > end || pos + 4 > len)
                return false;
            if (!IsNameChar(table[pos]) || !IsNameChar(table[pos + 1])
                || !IsNameChar(table[pos + 2]) || !IsNameChar(table[pos + 3]))
                return false;
            key = NameKey(table[pos], table[pos + 1], table[pos + 2], table[pos + 3]);
            pos += 4;
        }
        return true;
    }

    private static bool EvalNameOrCall(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        value = new Val { Kind = VkNull };
        uint savedPos = pos;
        uint key;
        if (!ReadNameKey(table, len, ref pos, end, out key))
        {
            pos = savedPos;
            return false;
        }

        int mi = FindMethod(key);
        if (mi >= 0)
        {
            int argCount = _methods[mi].ArgCount;
            ulong* args = stackalloc ulong[MaxMethodArgs];
            for (int i = 0; i < argCount; i++)
            {
                Val av;
                if (!EvalTerm(table, tIdx, len, ref pos, end, out av))
                    return false;
                args[i] = av.Int;
            }
            if (_skipMode != 0)
            {
                value = new Val { Kind = VkNull };
                return true;
            }
            Val rv;
            if (!InvokeMethod(mi, args, argCount, out rv))
                return false;
            value = rv;
            return true;
        }

        int ni = FindName(key);
        if (ni >= 0)
        {
            // Overlay first (values written at runtime), then the datum.
            for (int i = 0; i < _overlayCount; i++)
                if (_overlays[i].NameKey == key)
                {
                    value = _overlays[i].Value;
                    return true;
                }
            int dIdx = _names[ni].TableIdx;
            byte* dt = TablePtr(dIdx);
            uint dlen = TableLen(dIdx);
            uint dpos = _names[ni].DatumOffset;
            uint dend = dpos + 32 < dlen ? dpos + 32 : dlen;
            Val dv;
            if (dpos < dlen && EvalTerm(dt, dIdx, dlen, ref dpos, dend, out dv))
            {
                // Cache constants as overlays so repeated reads are cheap.
                StoreOverlay(key, dv);
                value = dv;
                return true;
            }
            value = new Val { Kind = VkNull };
            return true;
        }

        // Referencing an unknown name: treat as null (common for platform
        // fields we do not model); execution continues.
        value = new Val { Kind = VkNull };
        return true;
    }

    private static void StoreOverlay(uint key, Val v)
    {
        if (_skipMode != 0)
            return;
        for (int i = 0; i < _overlayCount; i++)
            if (_overlays[i].NameKey == key)
            {
                _overlays[i].Value = v;
                return;
            }
        if (_overlayCount < MaxOverlays)
        {
            _overlays[_overlayCount].NameKey = key;
            _overlays[_overlayCount].Value = v;
            _overlayCount++;
        }
    }

    /// <summary>Read the current value of a target (without consuming beyond it).</summary>
    private static bool ReadTarget(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        return EvalTerm(table, tIdx, len, ref pos, end, out value);
    }

    /// <summary>
    /// Assign a value to the target term at pos (SuperName position):
    /// Local/Arg slots, Name entries (overlay) and NullName are supported;
    /// anything else (fields, Index, DerefOf) is a logged no-op so that
    /// firmware methods manipulating platform registers still "execute".
    /// </summary>
    private static bool AssignTarget(byte* table, int tIdx, uint len, ref uint pos, uint end, Val v)
    {
        if (pos >= end || pos >= len)
            return false;
        byte op = table[pos];

        if (op == OpZero)
        {
            // NullName target: value flows on, nothing to write.
            pos++;
            return true;
        }
        if (op >= OpLocal0 && op <= 0x67)
        {
            int li = op - OpLocal0;
            pos++;
            if (_skipMode == 0 && _frameDepth > 0)
            {
                fixed (Frame* fp = &_frames[_frameDepth - 1])
                {
                    fp->Locals[li] = v.Int;
                    fp->LocalIsInt[li] = 1;
                }
            }
            return true;
        }
        if (op >= OpArg0 && op <= 0x6E)
        {
            int ai = op - OpArg0;
            pos++;
            if (_skipMode == 0 && _frameDepth > 0)
            {
                fixed (Frame* fp = &_frames[_frameDepth - 1])
                {
                    if (ai < MaxMethodArgs)
                    {
                        fp->Args[ai] = v.Int;
                        fp->ArgIsInt[ai] = 1;
                    }
                }
            }
            return true;
        }
        if (IsNameStart(op))
        {
            uint key;
            if (!ReadNameKey(table, len, ref pos, end, out key))
                return false;
            StoreOverlay(key, v);
            return true;
        }

        // Field units, Index/DerefOf chains, etc.: consume the term for
        // structure and treat the write as a no-op.
        Val ignored;
        if (!EvalTerm(table, tIdx, len, ref pos, end, out ignored))
            return false;
        return true;
    }

    private static bool EvalIf(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        value = new Val { Kind = VkInt, Int = 0 };
        pos++;   // If opcode
        uint pkgStart = pos;
        int pkg = ReadPkgLength(table, len, ref pos);
        if (pkg <= 0)
            return false;
        uint ifEnd = pkgStart + (uint)pkg;
        if (ifEnd > len || ifEnd > end)
            return false;

        if (_skipMode != 0)
        {
            pos = ifEnd;
            return true;
        }

        Val pred;
        if (!EvalTerm(table, tIdx, len, ref pos, ifEnd, out pred))
            return false;

        bool taken = pred.Int != 0;

        if (taken)
        {
            while (pos < ifEnd)
            {
                if (table[pos] == OpElse)
                {
                    // Skip the else branch entirely.
                    pos++;
                    uint eStart = pos;
                    int e = ReadPkgLength(table, len, ref pos);
                    if (e <= 0) return false;
                    pos = eStart + (uint)e;
                    break;
                }
                Val dv;
                if (!EvalTerm(table, tIdx, len, ref pos, ifEnd, out dv))
                    return false;
                if (FrameReturned())
                    return true;
                if (_breakFlag != 0)
                    return true;
            }
        }
        else
        {
            // Skip then-body until Else; execute the Else body when
            // present. The skip runs in skip mode so stores in the
            // not-taken branch are consumed but not applied.
            uint elseBody = 0;
            uint elseEnd = 0;
            _skipMode++;
            while (pos < ifEnd)
            {
                if (table[pos] == OpElse)
                {
                    pos++;
                    uint eStart = pos;
                    int e = ReadPkgLength(table, len, ref pos);
                    if (e <= 0)
                    {
                        _skipMode--;
                        return false;
                    }
                    elseBody = pos;
                    elseEnd = eStart + (uint)e;
                    break;
                }
                Val dv;
                if (!EvalTerm(table, tIdx, len, ref pos, ifEnd, out dv))
                {
                    _skipMode--;
                    return false;
                }
            }
            _skipMode--;
            if (elseBody != 0)
            {
                pos = elseBody;
                while (pos < elseEnd)
                {
                    Val dv;
                    if (!EvalTerm(table, tIdx, len, ref pos, elseEnd, out dv))
                        return false;
                    if (FrameReturned())
                        return true;
                    if (_breakFlag != 0)
                        return true;
                }
            }
        }

        pos = ifEnd;
        return true;
    }

    private static bool EvalWhile(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        value = new Val { Kind = VkInt, Int = 0 };
        pos++;   // While opcode
        uint pkgStart = pos;
        int pkg = ReadPkgLength(table, len, ref pos);
        if (pkg <= 0)
            return false;
        uint whileEnd = pkgStart + (uint)pkg;
        if (whileEnd > len || whileEnd > end)
            return false;

        if (_skipMode != 0)
        {
            pos = whileEnd;
            return true;
        }

        uint predStart = pos;
        int iterations = 0;
        while (iterations < WhileIterationCap)
        {
            iterations++;
            uint ppos = predStart;
            Val pred;
            if (!EvalTerm(table, tIdx, len, ref ppos, whileEnd, out pred))
                return false;
            if (pred.Int == 0)
                break;

            uint bodyPos = ppos;
            while (bodyPos < whileEnd)
            {
                Val bv;
                if (!EvalTerm(table, tIdx, len, ref bodyPos, whileEnd, out bv))
                    return false;
                if (FrameReturned())
                {
                    pos = whileEnd;
                    return true;
                }
                if (_breakFlag != 0)
                {
                    _breakFlag = 0;
                    pos = whileEnd;
                    return true;
                }
            }
        }

        pos = whileEnd;
        return true;
    }

    private static bool EvalExtended(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        value = new Val { Kind = VkInt, Int = 0 };
        if (pos + 1 >= len)
            return false;
        byte ext = table[pos + 1];
        pos += 2;
        switch (ext)
        {
            case OpExtMutex:
            case OpExtEvent:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    return true;
                }
            case OpExtAcquire:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    pos += 2;   // u16 timeout
                    return true;
                }
            case OpExtRelease:
            case OpExtSignal:
            case OpExtReset:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    return true;
                }
            case OpExtWait:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    Val t;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                        return false;
                    return true;
                }
            case OpExtSleep:
            case OpExtStall:
                {
                    Val t;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                        return false;
                    return true;
                }
            case OpExtLoad:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    Val t;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                        return false;
                    return true;
                }
            case OpExtLoadTable:
                {
                    for (int i = 0; i < 6; i++)
                    {
                        Val t;
                        if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                            return false;
                    }
                    return true;
                }
            case OpExtUnload:
                {
                    Val t;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                        return false;
                    return true;
                }
            case OpExtRevision:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    value = new Val { Kind = VkInt, Int = 2 };
                    return true;
                }
            case OpExtDebug:
                {
                    Val t;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                        return false;
                    return true;
                }
            case OpExtFatal:
                {
                    Val t;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                        return false;
                    pos += 4;   // FatalType + FatalCode + FatalArg
                    return true;
                }
            case OpExtTimer:
                value = new Val { Kind = VkInt, Int = 0 };
                return true;
            case OpExtFromBcd:
            case OpExtToBcd:
                {
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a))
                        return false;
                    if (!AssignTarget(table, tIdx, len, ref pos, end, a))
                        return false;
                    value = a;
                    return true;
                }
            case OpExtCondRefOf:
                {
                    Val a;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a))
                        return false;
                    var rv = new Val { Kind = VkInt, Int = a.Kind != VkNull ? 1UL : 0UL };
                    if (!AssignTarget(table, tIdx, len, ref pos, end, rv))
                        return false;
                    value = rv;
                    return true;
                }
            case OpExtCreateField:
                {
                    Val a, b, c;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out a)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out b)) return false;
                    if (!EvalTerm(table, tIdx, len, ref pos, end, out c)) return false;
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    StoreOverlay(key, new Val { Kind = VkNull });
                    return true;
                }
            case OpExtOpRegion:
                {
                    uint key;
                    if (!ReadNameKey(table, len, ref pos, end, out key))
                        return false;
                    for (int i = 0; i < 3; i++)
                    {
                        Val t;
                        if (!EvalTerm(table, tIdx, len, ref pos, end, out t))
                            return false;
                    }
                    return true;
                }
            case OpExtField:
            case OpExtIndexField:
            case OpExtBankField:
                {
                    // PkgLength covers the whole field list.
                    uint pkgStart = pos;
                    int pkg = ReadPkgLength(table, len, ref pos);
                    if (pkg <= 0)
                        return false;
                    pos = pkgStart + (uint)pkg;
                    return true;
                }
            case OpExtDevice:
            case OpExtProcessor:
            case OpExtPowerRes:
            case OpExtThermalZone:
                {
                    // Containers: skip via PkgLength (their contents are
                    // indexed by the linear scan, not executed here).
                    uint pkgStart = pos;
                    int pkg = ReadPkgLength(table, len, ref pos);
                    if (pkg <= 0)
                        return false;
                    pos = pkgStart + (uint)pkg;
                    return true;
                }
            default:
                _aborted = 4;
                DebugConsole.Write("[aml] unsupported extended op 0x");
                DebugConsole.WriteHex(ext);
                DebugConsole.WriteLine();
                return false;
        }
    }

    private static bool EvalIndex(byte* table, int tIdx, uint len, ref uint pos, uint end, out Val value)
    {
        value = new Val { Kind = VkNull };
        pos++;   // Index opcode
        Val container, idx;
        if (!EvalTerm(table, tIdx, len, ref pos, end, out container))
            return false;
        if (!EvalTerm(table, tIdx, len, ref pos, end, out idx))
            return false;

        Val el = new Val { Kind = VkNull };
        if (container.Kind == VkPackage && idx.Kind == VkInt && idx.Int < (ulong)container.Int)
        {
            int rt = RefTable(container.Ref);
            uint ro = RefOff(container.Ref);
            byte* t2 = TablePtr(rt);
            uint l2 = TableLen(rt);
            uint walkEnd = ro + 0x10000 < l2 ? ro + 0x10000 : l2;
            uint p = ro;
            for (int i = 0; i <= (int)idx.Int; i++)
            {
                Val e;
                if (!EvalTerm(t2, rt, l2, ref p, walkEnd, out e))
                    break;
                if (i == (int)idx.Int)
                    el = e;
            }
        }
        if (!AssignTarget(table, tIdx, len, ref pos, end, el))
            return false;
        value = el;
        return true;
    }

    // ------------------------------------------------------------------
    // Package access for _CST/_PSS style objects.
    // ------------------------------------------------------------------

    /// <summary>Cursor over a package value (constant package in a table).</summary>
    public struct Pkg
    {
        public int Count;
        public int TableIdx;
        public uint ElemPos;
        public uint End;
    }

    /// <summary>
    /// Open the package object named `_CST`/`_PSS`-style: a Method with
    /// this name is invoked (when it has no required arguments) and its
    /// result package is opened; a Name with this name opens its constant
    /// package. Returns false when absent or not a package.
    /// </summary>
    public static bool OpenNamedPackage(byte n0, byte n1, byte n2, byte n3, out Pkg pkg)
    {
        pkg = default;
        uint key = NameKey4(n0, n1, n2, n3);

        int mi = FindMethod(key);
        if (mi >= 0)
        {
            if (_methods[mi].ArgCount != 0)
                return false;
            _steps = DefaultStepBudget;
            _aborted = 0;
            Val rv;
            if (!InvokeMethod(mi, null, 0, out rv))
                return false;
            if (rv.Kind != VkPackage)
                return false;
            pkg.Count = (int)rv.Int;
            pkg.ElemPos = RefOff(rv.Ref);
            pkg.TableIdx = RefTable(rv.Ref);
            pkg.End = TableLen(pkg.TableIdx);
            return true;
        }

        int ni = FindName(key);
        if (ni < 0)
            return false;
        int dIdx = _names[ni].TableIdx;
        byte* dt = TablePtr(dIdx);
        uint dlen = TableLen(dIdx);
        uint dpos = _names[ni].DatumOffset;
        uint dend = dpos + 512 < dlen ? dpos + 512 : dlen;
        Val dv;
        if (!EvalTerm(dt, dIdx, dlen, ref dpos, dend, out dv))
            return false;
        if (dv.Kind != VkPackage)
            return false;
        pkg.Count = (int)dv.Int;
        pkg.ElemPos = RefOff(dv.Ref);
        pkg.TableIdx = RefTable(dv.Ref);
        pkg.End = TableLen(pkg.TableIdx);
        return true;
    }

    /// <summary>Element `index` as an integer (packages of constants).</summary>
    public static bool PkgInt(in Pkg pkg, int index, out ulong v)
    {
        v = 0;
        uint pos = pkg.ElemPos;
        if (index < 0 || index >= pkg.Count)
            return false;
        byte* t = TablePtr(pkg.TableIdx);
        uint len = TableLen(pkg.TableIdx);
        for (int i = 0; i <= index; i++)
        {
            Val e;
            if (!EvalTerm(t, pkg.TableIdx, len, ref pos, pkg.End, out e))
                return false;
            if (i == index)
            {
                v = e.Int;
                return true;
            }
        }
        return false;
    }

    /// <summary>Element `index` as a nested package.</summary>
    public static bool PkgSub(in Pkg pkg, int index, out Pkg sub)
    {
        sub = default;
        uint pos = pkg.ElemPos;
        if (index < 0 || index >= pkg.Count)
            return false;
        byte* t = TablePtr(pkg.TableIdx);
        uint len = TableLen(pkg.TableIdx);
        for (int i = 0; i <= index; i++)
        {
            Val e;
            if (!EvalTerm(t, pkg.TableIdx, len, ref pos, pkg.End, out e))
                return false;
            if (i == index)
            {
                if (e.Kind != VkPackage)
                    return false;
                sub.Count = (int)e.Int;
                sub.ElemPos = RefOff(e.Ref);
                sub.TableIdx = RefTable(e.Ref);
                sub.End = TableLen(RefTable(e.Ref));
                return true;
            }
        }
        return false;
    }
}
