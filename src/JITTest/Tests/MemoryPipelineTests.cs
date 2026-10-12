// JITTest - Memory<T>, ArrayPool<T>, ReadOnlySequence<T> and
// System.IO.Pipelines tests (Kestrel port milestone M2).
//
// These tests vet the new korlib primitives on-device (Tier-0 JIT):
//   - Memory/Span interplay (including the Span array-offset ctor),
//   - ArrayPool rent/return/reuse,
//   - ReadOnlySequence over linked segments,
//   - Pipe commit/consume semantics, backpressure and a 1 MB pump,
//   - async state machine + ValueTask awaiter with sync completions.

using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace JITTest;

public static class MemoryPipelineTests
{
    public static void RunAll()
    {
        RunStep("vt/make-tcs", ProbeMakeTcs);
        RunStep("vt/read-task", ProbeReadTask);
        RunStep("vt/wrap-vt", ProbeMakeVt);
        RunStep("vt/pending-flags", ProbePending);
        RunStep("vt/complete", ProbeComplete);
        RunStep("vt/done-flags", ProbeDone);
        RunStep("vt/return24", TestReturn24);
        RunStep("vt/copy24", TestCopy24);
        RunStep("vt-awaiter/copy", ProbeAwaiterCopy);
        RunStep("step/memory", TestMemoryBasics);
        RunStep("step/memory-span", TestMemorySpan);
        RunStep("step/array-pool", TestArrayPool);
        RunStep("seq/build-length-first", TestSeqBuildLengthFirst);
        RunStep("seq/slice", TestSeqSlice);
        RunStep("seq/checksum", TestSeqChecksum);
        RunStep("seq/getpos", TestSeqGetPos);
        RunStep("seq/single-empty", TestSeqSingleEmpty);
        RunStep("step/pipe-read-wake", TestPipeReaderAsyncWake);
        RunStep("step/pipe-pump-1mb", TestPipePump1MB);
        RunStep("step/pipe-backpressure", TestPipeBackpressure);
        RunStep("step/pipe-async-sync", TestPipeAsyncAwaitSyncCompletion);
        RunStep("step/task-suspension", TestTaskSuspension);
        RunStep("step/duplex-pipe", TestDuplexPipe);
    }

    /// <summary>Runs one test step; a throwing step is recorded as a failure
    /// instead of aborting the whole category (a silent category abort hid
    /// the ReadOnlySequence bug for the whole M2 era).</summary>
    private static void RunStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            TestTracker.Record(name, false, "ArgumentOutOfRangeException: " + ex.Message);
        }
        catch (ArgumentNullException ex)
        {
            TestTracker.Record(name, false, "ArgumentNullException: " + ex.Message);
        }
        catch (NullReferenceException ex)
        {
            TestTracker.Record(name, false, "NullReferenceException: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            TestTracker.Record(name, false, "InvalidOperationException: " + ex.Message);
        }
        catch (Exception ex)
        {
            TestTracker.Record(name, false, "Exception: " + ex.Message);
        }
    }

    // ==================== Memory ====================

    private static void TestMemoryBasics()
    {
        var arr = new byte[10];
        Memory<byte> mem = arr;                  // implicit conversion
        TestTracker.Record("Memory/implicit-wrap", mem.Length == 10 && !mem.IsEmpty);

        mem.Span[3] = 42;
        TestTracker.Record("Memory/indexer", arr[3] == 42 && mem.Span[3] == 42);

        var slice = mem.Slice(2, 5);
        TestTracker.Record("Memory/slice", slice.Length == 5 && slice.Span[1] == 42);

        var copy = new byte[5];
        slice.CopyTo(copy);
        TestTracker.Record("Memory/copyto", copy[1] == 42);

        var round = slice.ToArray();
        TestTracker.Record("Memory/toarray", round.Length == 5 && round[1] == 42);

        ReadOnlyMemory<byte> rom = mem;          // implicit conversion
        TestTracker.Record("Memory/implicit-readonly", rom.Length == 10 && rom.Span[3] == 42);

        TestTracker.Record("Memory/empty", Memory<byte>.Empty.IsEmpty);

        var eqSlice = mem.Slice(0, 10);
        var eqOther = new Memory<byte>(arr, 0, 10);
        TestTracker.Record("Memory/equality", eqSlice.Equals(eqOther));
        TestTracker.Record("Memory/eq-self", eqOther.Equals(eqOther));

        // Object-override path: the JIT-compiled override must be dispatched
        // with 'this' adjusted to the boxed struct's payload (not the box
        // header), and the constrained-callvirt receiver box must contain the
        // FULL struct (regression for boxed value-type virtual dispatch of
        // large structs - previously the length field was left zeroed and the
        // override compared MT pointers and array refs as struct fields).
        TestTracker.Record("Memory/eq-obj", eqSlice.Equals((object)eqOther));
        TestTracker.Record("Memory/eq-obj-self", eqSlice.Equals((object)eqSlice));
        TestTracker.Record("Memory/eq-obj-null", !eqSlice.Equals((object)null!));

        object boxedEq = eqSlice;
        TestTracker.Record("Memory/isinst", boxedEq is Memory<byte>);
        TestTracker.Record("Memory/isinst-eq", boxedEq is Memory<byte> m2 && m2.Equals(eqOther));
        TestTracker.Record("Memory/static-eq", object.Equals(eqSlice, eqOther));
        TestTracker.Record("Memory/hash-eq", eqOther.GetHashCode() == eqSlice.GetHashCode());

        var p1 = new SequencePosition(null, 5);
        var p2 = p1;
        TestTracker.Record("SeqPos/eq-self", p1.Equals(p1));
        TestTracker.Record("SeqPos/eq-copy", p1.Equals(p2));
    }

    private static void TestMemorySpan()
    {
        var arr = new byte[16];
        Memory<byte> mem = new Memory<byte>(arr, 4, 8);
        var span = mem.Span;
        TestTracker.Record("Memory/span-length", span.Length == 8, "len=" + span.Length.ToString());

        span[0] = 7;
        span[7] = 9;
        TestTracker.Record("Memory/span-write-through", arr[4] == 7 && arr[11] == 9);

        ReadOnlyMemory<byte> rom = new ReadOnlyMemory<byte>(arr, 6, 4);
        var rspan = rom.Span;
        TestTracker.Record("Memory/rospan", rspan.Length == 4 && rspan[0] == arr[6]);
    }

    // ==================== ArrayPool ====================

    private static void TestArrayPool()
    {
        var pool = ArrayPool<byte>.Shared;

        var a = pool.Rent(100);
        TestTracker.Record("ArrayPool/rent-min", a.Length >= 100, "len=" + a.Length.ToString());
        pool.Return(a);

        // Same size class (both round to the 128-byte bucket): the returned
        // buffer must be handed back out. (A 150-byte rent rounds to the
        // 256-byte bucket and legitimately could not reuse a 128-byte buffer.)
        var b = pool.Rent(100);
        TestTracker.Record("ArrayPool/reuse-same-buffer", ReferenceEquals(a, b),
            "a.Len=" + a.Length.ToString() + " b.Len=" + b.Length.ToString());
        pool.Return(b);

        var big = pool.Rent(70000);
        TestTracker.Record("ArrayPool/oversize-alloc", big.Length >= 70000);
        pool.Return(big);

        var c = pool.Rent(64);
        for (int i = 0; i < c.Length; i++)
            c[i] = 0x5A;
        pool.Return(c, true);
        var d = pool.Rent(64);
        TestTracker.Record("ArrayPool/clear-on-return", d.Length >= 64 && d[0] == 0 && d[63] == 0);
        pool.Return(d);
    }

    // Tier-0 coverage for the large-TResult Task/ValueTask paths: a ~48-byte
    // ReadResult used to NRE the whole flow (TaskCompletionSource<ReadResult>
    // / ValueTask<ReadResult> / a typed TResult storage field). The sub-steps
    // are individually labeled so a regression pinpoints the layer; they
    // pass with the boxed-result storage in korlib Task<TResult>/
    // ValueTask<TResult> (object field instead of a typed TResult field).
    private static TaskCompletionSource<ReadResult> _probeTcs;
    private static Task<ReadResult> _probeTask;
    private static ValueTask<ReadResult> _probeVt;

    private static void ProbeMakeTcs()
    {
        _probeTcs = new TaskCompletionSource<ReadResult>();
    }

    private static void ProbeReadTask()
    {
        _probeTask = _probeTcs.Task;
    }

    private static void ProbeMakeVt()
    {
        _probeVt = new ValueTask<ReadResult>(_probeTask);
    }

    private static void ProbePending()
    {
        TestTracker.Record("VT/large-pending-not-completed", !_probeVt.IsCompleted);
    }

    private static void ProbeComplete()
    {
        _probeTcs.TrySetResult(new ReadResult(default, false, true));
    }

    private static void ProbeDone()
    {
        bool done = _probeVt.IsCompleted;
        TestTracker.Record("VT/large-completed-after-set", done);
        ReadResult r = _probeVt.Result;   // exercises the unbox result path
        TestTracker.Record("VT/large-result-read-ok", r.IsCompleted);
    }

    // ---- 24-byte struct copy probes: the >=24B struct return goes through
    // the hidden-buffer path; ReadOnlySequence (48B) copies through this path
    // were observed to garble, so isolate the return leg (return24) and the
    // by-value arg + return leg (copy24) separately. ----
    private struct Triple24
    {
        public long a;
        public long b;
        public long c;
    }

    private static Triple24 MakeTriple24(long a, long b, long c)
    {
        var t = default(Triple24);
        t.a = a;
        t.b = b;
        t.c = c;
        return t;
    }

    private static Triple24 CopyTriple24(Triple24 v)
    {
        return v;
    }

    private static void TestReturn24()
    {
        var p = MakeTriple24(11, 22, 33);   // 24B struct return (hidden buffer)
        TestTracker.Record("VT/return24", p.a == 11 && p.b == 22 && p.c == 33,
            "a=" + p.a.ToString() + " b=" + p.b.ToString() + " c=" + p.c.ToString());
    }

    private static void TestCopy24()
    {
        var v = default(Triple24);
        v.a = 7;
        v.b = 8;
        v.c = 9;
        var w = CopyTriple24(v);            // 24B by-value arg + 24B return
        TestTracker.Record("VT/copy24", w.a == 7 && w.b == 8 && w.c == 9,
            "a=" + w.a.ToString() + " b=" + w.b.ToString() + " c=" + w.c.ToString());
    }

    // ---- ValueTask<ReadResult> (24B) awaiter copy probe: GetAwaiter returns
    // the 24B awaiter BY VALUE (hidden buffer), and the await path copies it
    // into a state machine local. Both pipe failures go through this shape. ----
    private static void ProbeAwaiterCopy()
    {
        var tcs = new TaskCompletionSource<ReadResult>();
        var vt = new ValueTask<ReadResult>(tcs.Task);
        var aw = vt.GetAwaiter();           // 24B awaiter value copy
        TestTracker.Record("vt-awaiter/pending-copy", !aw.IsCompleted);
        tcs.TrySetResult(new ReadResult(default, false, true));
        TestTracker.Record("vt-awaiter/copy-sees-completion", aw.IsCompleted);
        var r = aw.GetResult();
        TestTracker.Record("vt-awaiter/getresult-ok", r.IsCompleted);
    }

    // ==================== ReadOnlySequence ====================

    // NOTE on object->ReadOnlySequenceSegment<T> coercion: probed on-device
    // (2026-10-11) - `as`, explicit castclass and the `is` pattern ALL fail on
    // Tier-0 inside a generic context, for segments of any assembly. korlib
    // therefore recovers segments by reference identity against the chain
    // (FindByIdentity in ReadOnlySequence.cs); do not reintroduce casts.

    private sealed class TestSegment : ReadOnlySequenceSegment<int>
    {
        public TestSegment(int[] data, TestSegment? next)
        {
            Memory = data;
            Next = next;
            RunningIndex = 0;
        }

        /// <summary>Sets the cumulative index of this segment's first element
        /// (RunningIndex has a protected setter, so the test goes through a
        /// subclass helper).</summary>
        public void SetRunningIndex(long index)
        {
            RunningIndex = index;
        }
    }

    // The sequence checks are split into RunStep-able methods so a throwing
    // block pinpoints itself in the failure list instead of aborting the whole
    // category at its first fault (the old single "step/readonly-sequence"
    // step hid which of the five blocks threw).
    private static ReadOnlySequence<int> _seq;

    private static void TestSeqBuildLengthFirst()
    {
        var s1 = new int[] { 1, 2, 3, 4 };
        var s2 = new int[] { 5, 6, 7 };
        var s3 = new int[] { 8, 9 };
        var seg3 = new TestSegment(s3, null);
        var seg2 = new TestSegment(s2, seg3);
        var seg1 = new TestSegment(s1, seg2);

        // ReadOnlySequenceSegment<T> contract: RunningIndex is the index of
        // the segment's first element in the sequence, so creators must
        // chain them cumulatively (Length derives from it; Pipe does the
        // same). This test used to leave every segment at 0, making
        // Length=2 and Slice(3,4) throw - which silently aborted the whole
        // Memory/Pipelines category (see the RunCategory Report fix).
        seg1.SetRunningIndex(0);
        seg2.SetRunningIndex(seg1.RunningIndex + s1.Length);
        seg3.SetRunningIndex(seg2.RunningIndex + s2.Length);

        _seq = new ReadOnlySequence<int>(seg1, 0, seg3, 2);
        TestTracker.Record("Seq/multi-length", Assert.AreEqual((long)9, _seq.Length), "len=" + _seq.Length.ToString());
        TestTracker.Record("Seq/multi-first", Assert.AreEqual(1, _seq.First.Span[0]) && Assert.AreEqual(4, _seq.First.Length));
    }

    private static void TestSeqSlice()
    {
        var slice = _seq.Slice(3, 4);            // elements 4,5,6,7
        TestTracker.Record("Seq/slice-length", Assert.AreEqual((long)4, slice.Length));
        int segs = 0;
        long segLen = 0;
        foreach (var segMem in slice)
        {
            segs++;
            segLen += segMem.Length;
        }
        TestTracker.Record("Seq/slice-foreach", segs == 2 && segLen == 4,
            "segs=" + segs.ToString() + "/" + segLen.ToString());
        var arr = slice.ToArray();
        TestTracker.Record("Seq/slice-content", arr.Length == 4 && arr[0] == 4 && arr[3] == 7,
            "n=" + arr.Length.ToString());
    }

    private static void TestSeqChecksum()
    {
        long total = 0;
        int checksum = 0;          // direct .Span[i] on the foreach variable
        int localChecksum = 0;     // span copied to a local first
        int s0 = 0, s1 = 0, s2 = 0;
        int segNo = 0;
        foreach (var segmentMem in _seq)
        {
            total += segmentMem.Length;
            var sp = segmentMem.Span;
            int segLocal = 0;
            for (int i = 0; i < sp.Length; i++)
                segLocal += sp[i];
            for (int i = 0; i < segmentMem.Length; i++)
                checksum += segmentMem.Span[i];
            localChecksum += segLocal;
            if (segNo == 0) s0 = segLocal;
            else if (segNo == 1) s1 = segLocal;
            else if (segNo == 2) s2 = segLocal;
            segNo++;
        }
        TestTracker.Record("Seq/enumerate-total", total == 9, "total=" + total.ToString());
        TestTracker.Record("Seq/enumerate-checksum", checksum == 45,
            "sum=" + checksum.ToString()
            + " lsum=" + localChecksum.ToString()
            + " segL=" + s0.ToString() + "," + s1.ToString() + "," + s2.ToString());
        TestTracker.Record("Seq/enumerate-checksum-local-span", localChecksum == 45,
            "lsum=" + localChecksum.ToString());
    }

    private static void TestSeqGetPos()
    {
        var pos = _seq.GetPosition(5);           // element index 5 => value 6
        var viaSlice = _seq.Slice(pos);
        var viaArr = viaSlice.ToArray();
        TestTracker.Record("Seq/get-position", viaArr.Length == 4 && viaArr[0] == 6,
            "n=" + viaArr.Length.ToString());
    }

    private static void TestSeqSingleEmpty()
    {
        var single = new ReadOnlySequence<int>(new int[] { 10, 11, 12 });
        TestTracker.Record("Seq/single", single.Length == 3 && single.First.Span[1] == 11 && single.IsSingleSegment);
        TestTracker.Record("Seq/empty", new ReadOnlySequence<int>().IsEmpty);
    }

    // ==================== Pipe ====================

    private static void TestPipeReaderAsyncWake()
    {
        var pipe = new Pipe();
        var readTask = pipe.Reader.ReadAsync();  // no data yet -> pending
        TestTracker.Record("Pipe/read-async-incomplete-initially", !readTask.IsCompleted);

        var mem = pipe.Writer.GetMemory(5);
        for (int i = 0; i < 5; i++)
            mem.Span[i] = 0xAB;
        pipe.Writer.Advance(5);
        pipe.Writer.FlushAsync();

        TestTracker.Record("Pipe/read-async-woken-by-flush", readTask.IsCompleted);

        var awaiter = readTask.GetAwaiter();     // 24B awaiter value copy (probe)
        TestTracker.Record("Pipe/awaiter-copy-sees-completion", awaiter.IsCompleted);
        var result = awaiter.GetResult();
        int n = 0;
        foreach (var segmentMem in result.Buffer)
            n += segmentMem.Length;
        TestTracker.Record("Pipe/read-async-returns-flushed-data", n == 5, "n=" + n.ToString());

        pipe.Reader.AdvanceTo(result.Buffer.End);
        pipe.Reader.Complete();
        pipe.Writer.Complete();
    }

    private static void TestPipePump1MB()
    {
        const int Total = 1024 * 1024;
        const int Chunk = 4096;

        var pipe = new Pipe();
        var w = pipe.Writer;
        var r = pipe.Reader;

        int written = 0;
        int read = 0;
        bool orderOk = true;
        bool blocked = false;
        int guard = 0;

        while (read < Total && guard < 200000)
        {
            guard++;
            if (!blocked && written < Total)
            {
                var mem = w.GetMemory(Chunk);
                if (mem.Length < Chunk)
                {
                    orderOk = false;
                    break;
                }
                var memSpan = mem.Span;
                for (int i = 0; i < Chunk; i++)
                    memSpan[i] = (byte)((written + i) & 0xFF);
                w.Advance(Chunk);
                written += Chunk;
                var flush = w.FlushAsync();
                blocked = !flush.IsCompleted;
            }

            while (r.TryRead(out var result))
            {
                var buffer = result.Buffer;
                foreach (var segmentMem in buffer)
                {
                    int count = segmentMem.Length;
                    var segmentSpan = segmentMem.Span;
                    for (int i = 0; i < count; i++)
                    {
                        if (segmentSpan[i] != (byte)(read & 0xFF))
                            orderOk = false;
                        read++;
                    }
                }
                r.AdvanceTo(buffer.End);
            }

            if (blocked)
                blocked = false;                 // everything available was consumed above
        }

        TestTracker.Record("Pipe/1MB-pump-completed", read == Total && written == Total,
            "read=" + read.ToString() + " written=" + written.ToString());
        TestTracker.Record("Pipe/1MB-content-ordered", orderOk);

        r.Complete();
        w.Complete();
    }

    private static void TestPipeBackpressure()
    {
        var pipe = new Pipe();                   // pause 64 KiB, resume 32 KiB
        var w = pipe.Writer;
        var r = pipe.Reader;

        bool blocked = false;
        var blockedFlush = new ValueTask<FlushResult>(new FlushResult(false, false));
        int written = 0;

        for (int i = 0; i < 32 && !blocked; i++)
        {
            var mem = w.GetMemory(4096);
            var memSpan = mem.Span;
            for (int j = 0; j < 4096; j++)
                memSpan[j] = (byte)j;
            w.Advance(4096);
            written += 4096;
            var flush = w.FlushAsync();
            if (!flush.IsCompleted)
            {
                blocked = true;
                blockedFlush = flush;
            }
        }

        TestTracker.Record("Pipe/backpressure-blocks-writer", blocked, "written=" + written.ToString());

        while (r.TryRead(out var result))
            r.AdvanceTo(result.Buffer.End);

        TestTracker.Record("Pipe/backpressure-flush-resumed", blockedFlush.IsCompleted);

        r.Complete();
        w.Complete();
    }

    private static async Task<int> CountAsync(PipeReader reader)
    {
        var result = await reader.ReadAsync();
        int n = 0;
        var buffer = result.Buffer;
        foreach (var segmentMem in buffer)
            n += segmentMem.Length;
        reader.AdvanceTo(buffer.End);
        return n;
    }

    private static void TestPipeAsyncAwaitSyncCompletion()
    {
        var pipe = new Pipe();
        var mem = pipe.Writer.GetMemory(12);
        var memSpan = mem.Span;
        for (int i = 0; i < 12; i++)
            memSpan[i] = (byte)(i + 1);
        pipe.Writer.Advance(12);
        pipe.Writer.FlushAsync();

        var task = CountAsync(pipe.Reader);      // await completes synchronously
        int n = task.GetAwaiter().GetResult();
        TestTracker.Record("Pipe/async-await-sync-completion", n == 12, "n=" + n.ToString());

        pipe.Reader.Complete();
        pipe.Writer.Complete();
    }

    // The async Task method must stay SUSPENDED until the signal fires, and
    // the Task the caller holds must complete when it does. Regression guard
    // for the M3 async-delegate bug: the builder used to box the state
    // machine before materializing its Task, so the boxed builder completed
    // a different instance than builder.Task (the caller waited forever).
    private static async Task<int> SuspendOnSignalAsync(TaskCompletionSource<bool> signal)
    {
        await signal.Task;
        return 42;
    }

    private static void TestTaskSuspension()
    {
        var signal = new TaskCompletionSource<bool>();
        Task<int> task = SuspendOnSignalAsync(signal);
        TestTracker.Record("Task/suspended-before-signal", !task.IsCompleted);

        bool setOk = signal.TrySetResult(true);
        TestTracker.Record("Task/signal-set", setOk);
        TestTracker.Record("Task/completed-after-signal", task.IsCompleted);

        int result = task.GetAwaiter().GetResult();
        TestTracker.Record("Task/result-value", result == 42, "r=" + result.ToString());
    }

    // IDuplexPipe is the Kestrel-shaped transport view the M2 socket listener
    // hands to the HTTP layer: Input/Output must round-trip to the pipe ends.
    private static void TestDuplexPipe()
    {
        var pipe = new Pipe();
        IDuplexPipe duplex = new TestDuplex(pipe.Reader, pipe.Writer);
        TestTracker.Record("Pipelines/duplex-input", ReferenceEquals(duplex.Input, pipe.Reader));
        TestTracker.Record("Pipelines/duplex-output", ReferenceEquals(duplex.Output, pipe.Writer));

        pipe.Reader.Complete();
        pipe.Writer.Complete();
    }

    private sealed class TestDuplex : IDuplexPipe
    {
        public TestDuplex(PipeReader input, PipeWriter output)
        {
            Input = input;
            Output = output;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
    }
}
