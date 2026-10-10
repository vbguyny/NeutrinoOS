// NeutrinoOS korlib - ArrayPool<T> (Kestrel port milestone M2)
//
// Power-of-two bucketed array pool (16 bytes .. 64 KiB). Backs the
// System.IO.Pipelines segment allocator so steady-state pipe traffic does not
// allocate.
//
// Thread-safety note: the guard uses Interlocked, which is a non-atomic stub
// in the current korlib IL build. All current consumers (JIT tests, the
// cooperative service tick) are single-threaded; revisit when Interlocked is
// AOT-resolved for JIT code.

using System.Threading;

namespace System.Buffers
{
    public abstract class ArrayPool<T>
    {
        private static ArrayPool<T>? s_shared;

        public static ArrayPool<T> Shared
        {
            get
            {
                var pool = s_shared;
                if (pool == null)
                {
                    pool = new DefaultArrayPool<T>();
                    s_shared = pool;
                }
                return pool;
            }
        }

        public static ArrayPool<T> Create() => new DefaultArrayPool<T>();

        public static ArrayPool<T> Create(int maxArrayLength, int maxArraysPerBucket) =>
            new DefaultArrayPool<T>(maxArrayLength, maxArraysPerBucket);

        /// <summary>Retrieves a buffer at least <paramref name="minimumLength"/> long.</summary>
        public abstract T[] Rent(int minimumLength);

        /// <summary>Returns a buffer to the pool.</summary>
        public abstract void Return(T[] array, bool clearArray = false);
    }

    internal sealed class DefaultArrayPool<T> : ArrayPool<T>
    {
        private const int MinBucketSize = 16;
        private const int MaxBucketSize = 65536;
        private const int BucketCount = 13;          // 16 .. 65536
        private const int DefaultMaxPerBucket = 32;

        private readonly T[]?[][] _buckets = new T[]?[BucketCount][];
        private readonly int[] _counts = new int[BucketCount];
        private readonly int _maxArrayLength;
        private readonly int _maxPerBucket;
        private int _guard;

        public DefaultArrayPool()
            : this(MaxBucketSize, DefaultMaxPerBucket)
        {
        }

        public DefaultArrayPool(int maxArrayLength, int maxArraysPerBucket)
        {
            _maxArrayLength = maxArrayLength < MinBucketSize ? MinBucketSize : maxArrayLength;
            if (_maxArrayLength > MaxBucketSize)
                _maxArrayLength = MaxBucketSize;
            _maxPerBucket = maxArraysPerBucket < 1 ? 1 : maxArraysPerBucket;
        }

        public override T[] Rent(int minimumLength)
        {
            if (minimumLength < 0)
                throw new ArgumentOutOfRangeException("minimumLength");
            if (minimumLength == 0)
                return new T[0];
            if (minimumLength > _maxArrayLength)
                return new T[minimumLength];         // too large to pool

            int bucket = SelectBucket(minimumLength);
            Enter();
            int count = _counts[bucket];
            if (count > 0)
            {
                var bucketArr = _buckets[bucket]!;
                T[]? pooled = bucketArr[count - 1];
                bucketArr[count - 1] = null;
                _counts[bucket] = count - 1;
                Exit();
                if (pooled != null && pooled.Length >= minimumLength)
                    return pooled;
                return new T[BucketSize(bucket)];
            }
            Exit();
            return new T[BucketSize(bucket)];
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            if (array == null)
                throw new ArgumentNullException("array");
            if (array.Length < MinBucketSize || array.Length > _maxArrayLength)
                return;
            int bucket = SelectBucket(array.Length);
            if (BucketSize(bucket) != array.Length)
                return;                               // only exact bucket sizes are pooled

            if (clearArray)
            {
                for (int i = 0; i < array.Length; i++)
                    array[i] = default!;
            }

            Enter();
            var bucketArr = _buckets[bucket];
            if (bucketArr == null)
            {
                bucketArr = new T[]?[_maxPerBucket];
                _buckets[bucket] = bucketArr;
            }
            int count = _counts[bucket];
            if (count < bucketArr.Length)
            {
                bucketArr[count] = array;
                _counts[bucket] = count + 1;
            }
            Exit();
        }

        private static int SelectBucket(int size)
        {
            int bucket = 0;
            int bucketSize = MinBucketSize;
            while (bucketSize < size)
            {
                bucketSize <<= 1;
                bucket++;
            }
            return bucket;
        }

        private static int BucketSize(int bucket) => MinBucketSize << bucket;

        // Tiny non-blocking guard. See the thread-safety note at the top.
        private void Enter()
        {
            while (Interlocked.CompareExchange(ref _guard, 1, 0) != 0)
            {
            }
        }

        private void Exit()
        {
            Interlocked.Exchange(ref _guard, 0);
        }
    }
}
