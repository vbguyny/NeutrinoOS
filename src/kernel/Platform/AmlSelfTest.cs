// NeutrinoOS kernel - AML interpreter self-test (Phase 9 Task 4).
//
// QEMU q35 firmware exposes no _PTS/_WAK methods (verified by dumping
// the DSDT and disassembling it with iasl), so the AML interpreter is
// validated against hand-assembled AML with the exact encodings seen in
// that DSDT (If=0xA0, While=0xA2, Else=0xA1, Store=0x70, Add=0x72,
// LEqual=0x93, LLess=0x95, Increment=0x75, Return=0xA4).
//
// The synthetic table declares:
//   Name (TSTM, 0)
//   Method (TST1, 2) { Return (Add (Arg0, Arg1)) }
//   Method (TST2, 1) { If (Arg0 == 3) { Return (7) } Else { Return (9) } }
//   Method (TST3, 0) { Local0 = 5; While (Local0 < 10) { Local0++ } Return (Local0) }
//   Method (TST4, 1) { Store (Arg0, TSTM) }
//   Method (TST5, 0) { Return (TSTM) }
//
// Output lines are stable ("[AML] PASS ...") so acceptance scripts can
// assert on them.

using ProtonOS.Platform;

namespace ProtonOS.Platform;

/// <summary>Boot-time AML interpreter checks (see file header).</summary>
public static unsafe class AmlSelfTest
{
    private static int _pass;
    private static int _fail;

    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        DebugConsole.WriteLine("[AML] AML interpreter self-test:");

        // 36-byte fake ACPI header + AML body (121 bytes total).
        const uint HeaderLen = 36;
        const uint Total = 121;
        byte* blob = stackalloc byte[(int)Total];
        for (uint i = 0; i < Total; i++)
            blob[i] = 0;

        // Fake table header (signature + length only; scanner uses len).
        blob[0] = (byte)'T';
        blob[1] = (byte)'E';
        blob[2] = (byte)'S';
        blob[3] = (byte)'T';
        blob[4] = (byte)(Total & 0xFF);
        blob[5] = (byte)((Total >> 8) & 0xFF);

        uint p = HeaderLen;

        // Name (TSTM, 0)
        blob[p++] = 0x08;
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'M';
        blob[p++] = 0x00;

        // Method (TST1, 2) { Return (Add (Arg0, Arg1)) }
        blob[p++] = 0x14;
        blob[p++] = 0x0B;
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'1';
        blob[p++] = 0x02;
        blob[p++] = 0xA4;   // Return
        blob[p++] = 0x72;   // Add
        blob[p++] = 0x68;   // Arg0
        blob[p++] = 0x69;   // Arg1
        blob[p++] = 0x00;   // NullName target

        // Method (TST2, 1) { If (Arg0 == 3) { Return (7) } Else { Return (9) } }
        blob[p++] = 0x14;
        blob[p++] = 0x14;
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'2';
        blob[p++] = 0x01;
        blob[p++] = 0xA0;   // If
        blob[p++] = 0x0D;
        blob[p++] = 0x93;   // LEqual
        blob[p++] = 0x68;   // Arg0
        blob[p++] = 0x0A; blob[p++] = 0x03;
        blob[p++] = 0xA4;   // Return
        blob[p++] = 0x0A; blob[p++] = 0x07;
        blob[p++] = 0xA1;   // Else
        blob[p++] = 0x04;
        blob[p++] = 0xA4;   // Return
        blob[p++] = 0x0A; blob[p++] = 0x09;

        // Method (TST3, 0) { Local0 = 5; While (Local0 < 10) { Local0++ } Return (Local0) }
        blob[p++] = 0x14;
        blob[p++] = 0x14;
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'3';
        blob[p++] = 0x00;
        blob[p++] = 0x70;   // Store
        blob[p++] = 0x0A; blob[p++] = 0x05;
        blob[p++] = 0x60;   // Local0
        blob[p++] = 0xA2;   // While
        blob[p++] = 0x07;
        blob[p++] = 0x95;   // LLess
        blob[p++] = 0x60;   // Local0
        blob[p++] = 0x0A; blob[p++] = 0x0A;
        blob[p++] = 0x75;   // Increment
        blob[p++] = 0x60;   // Local0
        blob[p++] = 0xA4;   // Return
        blob[p++] = 0x60;   // Local0

        // Method (TST4, 1) { Store (Arg0, TSTM) }
        blob[p++] = 0x14;
        blob[p++] = 0x0C;
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'4';
        blob[p++] = 0x01;
        blob[p++] = 0x70;   // Store
        blob[p++] = 0x68;   // Arg0
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'M';

        // Method (TST5, 0) { Return (TSTM) }
        blob[p++] = 0x14;
        blob[p++] = 0x0B;
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'5';
        blob[p++] = 0x00;
        blob[p++] = 0xA4;   // Return
        blob[p++] = (byte)'T'; blob[p++] = (byte)'S'; blob[p++] = (byte)'T'; blob[p++] = (byte)'M';

        if (p != Total)
        {
            FailValue("synthetic blob length mismatch, bytes=", p);
            Summary();
            return;
        }

        Aml.ResetNamespace();
        if (!Aml.AddTable(blob, Total))
        {
            Fail("index synthetic table");
            Summary();
            return;
        }

        ulong* args = stackalloc ulong[2];
        args[0] = 5;
        args[1] = 7;
        ulong r;
        if (Aml.Invoke((byte)'T', (byte)'S', (byte)'T', (byte)'1', args, 2, out r) && r == 12)
            Pass("TST1 add-args = 12");
        else
            FailValue("TST1 add-args, got ", r);

        args[0] = 3;
        if (Aml.Invoke((byte)'T', (byte)'S', (byte)'T', (byte)'2', args, 1, out r) && r == 7)
            Pass("TST2 if-equal = 7");
        else
            FailValue("TST2 if-equal, got ", r);

        args[0] = 4;
        if (Aml.Invoke((byte)'T', (byte)'S', (byte)'T', (byte)'2', args, 1, out r) && r == 9)
            Pass("TST2 else = 9");
        else
            FailValue("TST2 else, got ", r);

        if (Aml.Invoke((byte)'T', (byte)'S', (byte)'T', (byte)'3', null, 0, out r) && r == 10)
            Pass("TST3 while = 10");
        else
            FailValue("TST3 while, got ", r);

        args[0] = 42;
        Aml.Invoke((byte)'T', (byte)'S', (byte)'T', (byte)'4', args, 1, out r);
        if (Aml.Invoke((byte)'T', (byte)'S', (byte)'T', (byte)'5', null, 0, out r) && r == 42)
            Pass("TST5 stored-name = 42");
        else
            FailValue("TST5 stored-name, got ", r);

        // Remove the synthetic table from the namespace so later users
        // (PowerManagement) index only the real firmware tables.
        Aml.ResetNamespace();

        Summary();
    }

    private static void Pass(string what)
    {
        _pass++;
        DebugConsole.Write("[AML] PASS ");
        DebugConsole.WriteLine(what);
    }

    private static void Fail(string what)
    {
        _fail++;
        DebugConsole.Write("[AML] FAIL ");
        DebugConsole.WriteLine(what);
    }

    private static void FailValue(string what, ulong v)
    {
        _fail++;
        DebugConsole.Write("[AML] FAIL ");
        DebugConsole.Write(what);
        DebugConsole.WriteDecimal((int)v);
        DebugConsole.WriteLine();
    }

    private static void Summary()
    {
        DebugConsole.Write("[AML] result: ");
        DebugConsole.WriteDecimal(_pass);
        DebugConsole.Write(" pass, ");
        DebugConsole.WriteDecimal(_fail);
        DebugConsole.WriteLine(" fail");
    }
}
