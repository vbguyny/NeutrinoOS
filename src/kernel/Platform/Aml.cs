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
}
