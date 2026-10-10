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
        TestMemoryBasics();
        TestMemorySpan();
        TestArrayPool();
        TestReadOnlySequence();
        TestPipeReaderAsyncWake();
        TestPipePump1MB();
        TestPipeBackpressure();
        TestPipeAsyncAwaitSyncCompletion();
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

    // ==================== ReadOnlySequence ====================

    private sealed class TestSegment : ReadOnlySequenceSegment<int>
    {
        public TestSegment(int[] data, TestSegment? next)
        {
            Memory = data;
            Next = next;
            RunningIndex = 0;
        }
    }

    private static void TestReadOnlySequence()
    {
        var s1 = new int[] { 1, 2, 3, 4 };
        var s2 = new int[] { 5, 6, 7 };
        var s3 = new int[] { 8, 9 };
        var seg3 = new TestSegment(s3, null);
        var seg2 = new TestSegment(s2, seg3);
        var seg1 = new TestSegment(s1, seg2);

        var seq = new ReadOnlySequence<int>(seg1, 0, seg3, 2);
        TestTracker.Record("Seq/multi-length", Assert.AreEqual((long)9, seq.Length), "len=" + seq.Length.ToString());
        TestTracker.Record("Seq/multi-first", Assert.AreEqual(1, seq.First.Span[0]) && Assert.AreEqual(4, seq.First.Length));

        var slice = seq.Slice(3, 4);             // elements 4,5,6,7
        TestTracker.Record("Seq/slice-length", Assert.AreEqual((long)4, slice.Length));
        var arr = slice.ToArray();
        TestTracker.Record("Seq/slice-content", arr.Length == 4 && arr[0] == 4 && arr[3] == 7);

        long total = 0;
        int checksum = 0;
        foreach (var segmentMem in seq)
        {
            total += segmentMem.Length;
            for (int i = 0; i < segmentMem.Length; i++)
                checksum += segmentMem.Span[i];
        }
        TestTracker.Record("Seq/enumerate-total", total == 9, "total=" + total.ToString());
        TestTracker.Record("Seq/enumerate-checksum", checksum == 45);

        var pos = seq.GetPosition(5);            // element index 5 => value 6
        var viaSlice = seq.Slice(pos);
        var viaArr = viaSlice.ToArray();
        TestTracker.Record("Seq/get-position", viaArr.Length == 4 && viaArr[0] == 6);

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

        var result = readTask.GetAwaiter().GetResult();
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
}
