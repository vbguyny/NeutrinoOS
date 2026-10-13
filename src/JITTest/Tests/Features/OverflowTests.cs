// JITTest - Overflow Tests
// Tests checked arithmetic operations (add.ovf, sub.ovf, mul.ovf, conv.ovf IL opcodes)

namespace JITTest;

/// <summary>
/// Overflow tests - verifies checked arithmetic operations.
/// Tests add.ovf, sub.ovf, mul.ovf, conv.ovf IL opcodes.
/// </summary>
public static class OverflowTests
{
    public static void RunAll()
    {
        // Discriminators first: a self-thrown OverflowException (software path,
        // both ends JIT-side) must catch; the hardware-trap probes show whether
        // caught CPU exceptions reach JIT catch funclets at all. The checked-
        // overflow tests then pin the INT 4 path specifically.
        Step("SelfThrowOverflowCatch", SelfThrowOverflowCatch);
        Step("HwNullDeref", HwNullDeref);
        Step("HwDivideByZero", HwDivideByZero);

        Step("AddNoOverflow", TestCheckedAddNoOverflow);
        Step("AddOverflow", TestCheckedAddOverflow);
        Step("AddOverflowCatchAll", TestCheckedAddOverflowCatchAll);
        Step("SubNoOverflow", TestCheckedSubNoOverflow);
        Step("SubOverflow", TestCheckedSubOverflow);
        Step("MulNoOverflow", TestCheckedMulNoOverflow);
        Step("MulOverflow", TestCheckedMulOverflow);
        Step("ConvNoOverflow", TestCheckedConvNoOverflow);
        Step("ConvOverflow", TestCheckedConvOverflow);
        Step("AddUnsignedNoOverflow", TestCheckedAddUnsignedNoOverflow);
        Step("AddUnsignedOverflow", TestCheckedAddUnsignedOverflow);
    }

    /// <summary>Runs one test; an escaping exception is recorded against that
    /// test and the category continues. The category previously died at the
    /// first NRE, hiding the outcome of every later test.</summary>
    private static void Step(string name, Action test)
    {
        try
        {
            test();
        }
        catch (OverflowException)
        {
            TestTracker.Record("overflow/" + name + "/escaping", false, "OverflowException");
        }
        catch (NullReferenceException)
        {
            TestTracker.Record("overflow/" + name + "/escaping", false, "NullReferenceException");
        }
        catch (InvalidOperationException)
        {
            TestTracker.Record("overflow/" + name + "/escaping", false, "InvalidOperationException");
        }
        catch (Exception)
        {
            TestTracker.Record("overflow/" + name + "/escaping", false, "Exception (other)");
        }
    }

    private static void TestCheckedAddNoOverflow()
    {
        int a = 20;
        int b = 22;
        int result = checked(a + b);
        TestTracker.Record("overflow.AddNoOverflow", result == 42);
    }

    private static void TestCheckedAddOverflow()
    {
        bool caught = false;
        NeutrinoOS.DDK.Kernel.Debug.Report("[JITTest] ovf-add-before");
        try
        {
            int a = int.MaxValue;
            int b = 1;
            int result = checked(a + b);
        }
        catch (OverflowException)
        {
            caught = true;
            NeutrinoOS.DDK.Kernel.Debug.Report("[JITTest] ovf-add-caught");
        }
        NeutrinoOS.DDK.Kernel.Debug.Report("[JITTest] ovf-add-after");
        TestTracker.Record("overflow.AddOverflow", caught);
    }

    /// <summary>Same checked overflow, but via the catch-all shape - tells a
    /// broken hardware-trap transfer apart from a broken OverflowException
    /// catch-type match.</summary>
    private static void TestCheckedAddOverflowCatchAll()
    {
        bool caught = false;
        try
        {
            int a = int.MaxValue;
            int b = 1;
            int result = checked(a + b);
        }
        catch (Exception)
        {
            caught = true;
        }
        TestTracker.Record("overflow.AddOverflowCatchAll", caught);
    }

    private static void TestCheckedSubNoOverflow()
    {
        int a = 50;
        int b = 8;
        int result = checked(a - b);
        TestTracker.Record("overflow.SubNoOverflow", result == 42);
    }

    private static void TestCheckedSubOverflow()
    {
        bool caught = false;
        try
        {
            int a = int.MinValue;
            int b = 1;
            int result = checked(a - b);
        }
        catch (OverflowException)
        {
            caught = true;
        }
        TestTracker.Record("overflow.SubOverflow", caught);
    }

    private static void TestCheckedMulNoOverflow()
    {
        int a = 6;
        int b = 7;
        int result = checked(a * b);
        TestTracker.Record("overflow.MulNoOverflow", result == 42);
    }

    private static void TestCheckedMulOverflow()
    {
        bool caught = false;
        try
        {
            int a = int.MaxValue;
            int b = 2;
            int result = checked(a * b);
        }
        catch (OverflowException)
        {
            caught = true;
        }
        TestTracker.Record("overflow.MulOverflow", caught);
    }

    private static void TestCheckedConvNoOverflow()
    {
        long value = 42;
        int result = checked((int)value);
        TestTracker.Record("overflow.ConvNoOverflow", result == 42);
    }

    private static void TestCheckedConvOverflow()
    {
        bool caught = false;
        try
        {
            long value = (long)int.MaxValue + 1;
            int result = checked((int)value);
        }
        catch (OverflowException)
        {
            caught = true;
        }
        TestTracker.Record("overflow.ConvOverflow", caught);
    }

    private static void TestCheckedAddUnsignedNoOverflow()
    {
        uint a = 20;
        uint b = 22;
        uint result = checked(a + b);
        TestTracker.Record("overflow.AddUnsignedNoOverflow", result == 42);
    }

    private static void TestCheckedAddUnsignedOverflow()
    {
        bool caught = false;
        try
        {
            uint a = uint.MaxValue;
            uint b = 1;
            uint result = checked(a + b);
        }
        catch (OverflowException)
        {
            caught = true;
        }
        TestTracker.Record("overflow.AddUnsignedOverflow", caught);
    }

    // ---- hardware-trap + software-throw probes ----

    /// <summary>Software-throw discriminator: both the thrown object and the
    /// catch clause are JIT-side here. If this catches but the INT 4 overflow
    /// path does not, the kernel-created OverflowException instance and the
    /// JIT-resolved catch type do not identify as the same type.</summary>
    private static void SelfThrowOverflowCatch()
    {
        bool caught = false;
        try
        {
            throw new OverflowException();
        }
        catch (OverflowException)
        {
            caught = true;
        }
        TestTracker.Record("mt/overflow-software-catch", caught);
    }

    private static void HwNullDeref()
    {
        // NOTE: string.Length goes through StringHelpers.GetLength, which
        // null-guards and returns 0 - use a RAW field load (plain `ldfld`
        // from a null this, emitted as a direct memory read) so the probe
        // really produces a #PF.
        bool caught = false;
        try
        {
            int l = s_nullTest.Field;      // ldfld null -> vector 14 (#PF)
            TestTracker.Record("hw/nullderef-no-trap", false, "l=" + l.ToString());
        }
        catch (NullReferenceException)
        {
            caught = true;
        }
        TestTracker.Record("hw/nullderef-catch", caught);

        // Discriminator: a SECOND hardware fault in the same frame - the
        // first catch transfers execution so this load runs with rsp =
        // rbp-0x100, which used to make the fault dispatch enter the catch
        // funclet one slot high (regression coverage for native.asm's
        // exception_funclet_entry_trampoline).
        bool caughtLoc = false;
        try
        {
            NulDerefProbe? n = null;
            int l2 = n!.Field;
            TestTracker.Record("nd/loc-no-trap", false, "l=" + l2.ToString());
        }
        catch (NullReferenceException)
        {
            caughtLoc = true;
        }
        TestTracker.Record("nd/loc-catch", caughtLoc);
    }

    private sealed class NulDerefProbe
    {
        public int Field;
    }

    private static NulDerefProbe s_nullTest = null!;

    private static void HwDivideByZero()
    {
        bool caught = false;
        try
        {
            int x = 10;
            int y = 0;
            int z = x / y;                 // divide by zero -> vector 0 (#DE)
            TestTracker.Record("hw/div0-no-trap", z == 0);
        }
        catch (DivideByZeroException)
        {
            caught = true;
        }
        TestTracker.Record("hw/div0-catch", caught);
    }
}
