// NeutrinoOS korlib - System.Linq.Enumerable
//
// Phase 4: LINQ to Objects for .NET 10 console applications.
//
// SCOPE
// -----
// This is the commonly used subset that console applications rely on:
// Where/Select/SelectMany, First/Last/Single (+OrDefault), Any/All/Count,
// ToList/ToArray, OrderBy/OrderByDescending/ThenBy/ThenByDescending,
// GroupBy, Join, Sum/Min/Max/Average, Distinct, Take/Skip, Concat,
// Contains, ElementAt, Reverse, Cast, Empty/Range/Repeat.
//
// IMPLEMENTATION NOTES
// --------------------
// * Deferred execution: operator methods return small enumerable classes
//   that pull from the source on each MoveNext; nothing is buffered until
//   an operator requires it (ordering, grouping, join lookups).
// * The operator enumerators are hand-written classes rather than
//   compiler-generated `yield return` iterators. Both forms are plain IL;
//   the hand-written form keeps the JIT surface to straight-line methods
//   (no iterator state-machine hoisting) so LINQ works on the Phase 4
//   Tier-0 JIT without relying on state-machine support. `yield return`
//   in application code is still exercised separately by the Phase 4
//   test suite.
// * Ordering is implemented with a stable insertion sort over the
//   buffered source - O(n^2) worst case, acceptable for the console-scale
//   sequences NeutrinoOS targets, and trivially stable (ties keep source
//   order), matching the official OrderBy semantics for equal keys.
// * Sorting keys are compared with Comparer<TKey>.Default or a caller
//   supplied IComparer<TKey>, exactly like the BCL.
// * Deviations from the official BCL (documented per member below):
//   no indexed overloads (Func<T,int,...>), no query-syntax-optional
//   operators beyond the listed set, no IQueryable/expression trees,
//   no parallelism (AsParallel), and no async LINQ (see
//   System.Linq.AsyncEnumerable for the Phase 4 load-compatibility stub).

using System.Collections;
using System.Collections.Generic;

namespace System.Linq;

/// <summary>
/// A sequence whose elements are ordered by one or more keys (the result
/// of OrderBy/ThenBy). NeutrinoOS implements the multi-key chaining used
/// by query syntax; the full official interface surface is a subset.
/// </summary>
public interface IOrderedEnumerable<TElement> : IEnumerable<TElement>
{
    /// <summary>
    /// Adds a secondary (tertiary, ...) sort key. Called by
    /// ThenBy/ThenByDescending - not intended to be called directly.
    /// </summary>
    IOrderedEnumerable<TElement> CreateOrderedEnumerable<TKey>(
        Func<TElement, TKey> keySelector, IComparer<TKey>? comparer, bool descending);
}

/// <summary>
/// A group of elements that share a common key (the element type produced
/// by GroupBy). NeutrinoOS implements the enumerable view and Key accessor.
/// </summary>
public interface IGrouping<out TKey, out TElement> : IEnumerable<TElement>
{
    /// <summary>Gets the key of the group.</summary>
    TKey Key { get; }
}

/// <summary>
/// Provides the LINQ to Objects operators supported by NeutrinoOS Phase 4
/// (see the file header for the supported set and deviations).
/// </summary>
public static class Enumerable
{
    // ==================== Helpers ====================

    private static void ThrowIfNull(object? arg, string name)
    {
        if (arg == null)
            throw new ArgumentNullException(name);
    }

    /// <summary>Returns an empty sequence (no allocation of the element buffer).</summary>
    public static IEnumerable<TResult> Empty<TResult>()
        => EmptyEnumerable<TResult>.Instance;

    private sealed class EmptyEnumerable<T> : IEnumerable<T>, IEnumerator<T>
    {
        public static readonly EmptyEnumerable<T> Instance = new EmptyEnumerable<T>();
        public IEnumerator<T> GetEnumerator() => this;
        public object Current => null!;
        T IEnumerator<T>.Current => default!;
        public bool MoveNext() => false;
        public void Reset() { }
        public void Dispose() { }
        IEnumerator IEnumerable.GetEnumerator() => this;
    }

    /// <summary>Generates a sequence of integral numbers within a range.</summary>
    public static IEnumerable<int> Range(int start, int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException("count");
        return new RangeEnumerable(start, count);
    }

    private sealed class RangeEnumerable : IEnumerable<int>, IEnumerator<int>
    {
        private readonly int _start;
        private readonly int _count;
        private int _index = -1;
        public RangeEnumerable(int start, int count) { _start = start; _count = count; }
        public IEnumerator<int> GetEnumerator() => new RangeEnumerable(_start, _count);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int Current => _start + _index;
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            if (_index + 1 >= _count) return false;
            _index++;
            return true;
        }
        public void Reset() { _index = -1; }
        public void Dispose() { }
    }

    /// <summary>Generates a sequence that contains one repeated value.</summary>
    public static IEnumerable<TResult> Repeat<TResult>(TResult element, int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException("count");
        return new RepeatEnumerable<TResult>(element, count);
    }

    private sealed class RepeatEnumerable<T> : IEnumerable<T>, IEnumerator<T>
    {
        private readonly T _element;
        private readonly int _count;
        private int _index = -1;
        public RepeatEnumerable(T element, int count) { _element = element; _count = count; }
        public IEnumerator<T> GetEnumerator() => new RepeatEnumerable<T>(_element, _count);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public T Current => _element;
        object IEnumerator.Current => Current!;
        public bool MoveNext()
        {
            if (_index + 1 >= _count) return false;
            _index++;
            return true;
        }
        public void Reset() { _index = -1; }
        public void Dispose() { }
    }

    // ==================== Filtering ====================

    /// <summary>Filters a sequence of values based on a predicate (deferred).</summary>
    public static IEnumerable<TSource> Where<TSource>(
        this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(predicate, "predicate");
        return new WhereEnumerable<TSource>(source, predicate);
    }

    private sealed class WhereEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;
        private readonly Func<T, bool> _predicate;
        public WhereEnumerable(IEnumerable<T> source, Func<T, bool> predicate)
        { _source = source; _predicate = predicate; }
        public IEnumerator<T> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _predicate);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly IEnumerator<T> _inner;
            private readonly Func<T, bool> _predicate;
            public Enumerator(IEnumerator<T> inner, Func<T, bool> predicate)
            { _inner = inner; _predicate = predicate; }
            public T Current => _inner.Current;
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                while (_inner.MoveNext())
                {
                    if (_predicate(_inner.Current))
                        return true;
                }
                return false;
            }
            public void Reset() => _inner.Reset();
            public void Dispose() => _inner.Dispose();
        }
    }

    /// <summary>Filters values that are assignable to <typeparamref name="TResult"/> (deferred).</summary>
    public static IEnumerable<TResult> OfType<TResult>(this IEnumerable source)
    {
        ThrowIfNull(source, "source");
        return new OfTypeEnumerable<TResult>(source);
    }

    private sealed class OfTypeEnumerable<TResult> : IEnumerable<TResult>
    {
        private readonly IEnumerable _source;
        public OfTypeEnumerable(IEnumerable source) { _source = source; }
        public IEnumerator<TResult> GetEnumerator() => new Enumerator(_source.GetEnumerator());
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<TResult>
        {
            private readonly IEnumerator _inner;
            private TResult _current = default!;
            public Enumerator(IEnumerator inner) { _inner = inner; }
            public TResult Current => _current;
            object IEnumerator.Current => _current!;
            public bool MoveNext()
            {
                while (_inner.MoveNext())
                {
                    if (_inner.Current is TResult match)
                    {
                        _current = match;
                        return true;
                    }
                }
                return false;
            }
            public void Reset() => _inner.Reset();
            public void Dispose() { if (_inner is IDisposable d) d.Dispose(); }
        }
    }

    /// <summary>
    /// Casts the elements of a sequence to <typeparamref name="TResult"/>
    /// (deferred; throws InvalidCastException at enumeration time on a bad element).
    /// </summary>
    public static IEnumerable<TResult> Cast<TResult>(this IEnumerable source)
    {
        ThrowIfNull(source, "source");
        return new CastEnumerable<TResult>(source);
    }

    private sealed class CastEnumerable<TResult> : IEnumerable<TResult>
    {
        private readonly IEnumerable _source;
        public CastEnumerable(IEnumerable source) { _source = source; }
        public IEnumerator<TResult> GetEnumerator() => new Enumerator(_source.GetEnumerator());
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<TResult>
        {
            private readonly IEnumerator _inner;
            public Enumerator(IEnumerator inner) { _inner = inner; }
            public TResult Current => (TResult)_inner.Current!;
            object IEnumerator.Current => Current!;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
            public void Dispose() { if (_inner is IDisposable d) d.Dispose(); }
        }
    }

    // ==================== Projection ====================

    /// <summary>Projects each element into a new form (deferred).</summary>
    public static IEnumerable<TResult> Select<TSource, TResult>(
        this IEnumerable<TSource> source, Func<TSource, TResult> selector)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(selector, "selector");
        return new SelectEnumerable<TSource, TResult>(source, selector);
    }

    private sealed class SelectEnumerable<TSource, TResult> : IEnumerable<TResult>
    {
        private readonly IEnumerable<TSource> _source;
        private readonly Func<TSource, TResult> _selector;
        public SelectEnumerable(IEnumerable<TSource> source, Func<TSource, TResult> selector)
        { _source = source; _selector = selector; }
        public IEnumerator<TResult> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _selector);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<TResult>
        {
            private readonly IEnumerator<TSource> _inner;
            private readonly Func<TSource, TResult> _selector;
            public Enumerator(IEnumerator<TSource> inner, Func<TSource, TResult> selector)
            { _inner = inner; _selector = selector; }
            public TResult Current => _selector(_inner.Current);
            object IEnumerator.Current => Current!;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
            public void Dispose() => _inner.Dispose();
        }
    }

    /// <summary>Projects each element to a sequence and flattens the results (deferred).</summary>
    public static IEnumerable<TResult> SelectMany<TSource, TResult>(
        this IEnumerable<TSource> source, Func<TSource, IEnumerable<TResult>> selector)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(selector, "selector");
        return new SelectManyEnumerable<TSource, TResult>(source, selector);
    }

    private sealed class SelectManyEnumerable<TSource, TResult> : IEnumerable<TResult>
    {
        private readonly IEnumerable<TSource> _source;
        private readonly Func<TSource, IEnumerable<TResult>> _selector;
        public SelectManyEnumerable(IEnumerable<TSource> source, Func<TSource, IEnumerable<TResult>> selector)
        { _source = source; _selector = selector; }
        public IEnumerator<TResult> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _selector);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<TResult>
        {
            private readonly IEnumerator<TSource> _outer;
            private readonly Func<TSource, IEnumerable<TResult>> _selector;
            private IEnumerator<TResult>? _inner;
            public Enumerator(IEnumerator<TSource> outer, Func<TSource, IEnumerable<TResult>> selector)
            { _outer = outer; _selector = selector; }
            public TResult Current => _inner!.Current;
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                for (;;)
                {
                    if (_inner != null && _inner.MoveNext())
                        return true;
                    if (_inner != null)
                    {
                        _inner.Dispose();
                        _inner = null;
                    }
                    if (!_outer.MoveNext())
                        return false;
                    IEnumerable<TResult> next = _selector(_outer.Current);
                    if (next == null)
                        throw new InvalidOperationException("SelectMany selector returned null");
                    _inner = next.GetEnumerator();
                }
            }
            public void Reset() { _inner = null; _outer.Reset(); }
            public void Dispose()
            {
                if (_inner != null) _inner.Dispose();
                _outer.Dispose();
            }
        }
    }

    // ==================== Element operators ====================

    /// <summary>Returns the first element, or throws if the sequence is empty.</summary>
    public static TSource First<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        IEnumerator<TSource> e = source.GetEnumerator();
        try
        {
            if (!e.MoveNext())
                throw new InvalidOperationException("Sequence contains no elements");
            return e.Current;
        }
        finally { e.Dispose(); }
    }

    /// <summary>Returns the first element matching the predicate, or throws.</summary>
    public static TSource First<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(predicate, "predicate");
        foreach (TSource item in source)
        {
            if (predicate(item))
                return item;
        }
        throw new InvalidOperationException("Sequence contains no matching element");
    }

    /// <summary>Returns the first element, or default(TSource) when empty.</summary>
    public static TSource? FirstOrDefault<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        IEnumerator<TSource> e = source.GetEnumerator();
        try
        {
            return e.MoveNext() ? e.Current : default;
        }
        finally { e.Dispose(); }
    }

    /// <summary>Returns the first matching element, or default(TSource) when none matches.</summary>
    public static TSource? FirstOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(predicate, "predicate");
        foreach (TSource item in source)
        {
            if (predicate(item))
                return item;
        }
        return default;
    }

    /// <summary>Returns the last element, or throws if the sequence is empty.</summary>
    public static TSource Last<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        bool found = false;
        TSource last = default!;
        foreach (TSource item in source)
        {
            last = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return last;
    }

    /// <summary>Returns the last element, or default(TSource) when empty.</summary>
    public static TSource? LastOrDefault<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        TSource? last = default;
        foreach (TSource item in source)
            last = item;
        return last;
    }

    /// <summary>Returns the only element, or throws when the sequence is empty or has more than one.</summary>
    public static TSource Single<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        IEnumerator<TSource> e = source.GetEnumerator();
        try
        {
            if (!e.MoveNext())
                throw new InvalidOperationException("Sequence contains no elements");
            TSource result = e.Current;
            if (e.MoveNext())
                throw new InvalidOperationException("Sequence contains more than one element");
            return result;
        }
        finally { e.Dispose(); }
    }

    /// <summary>Returns the only element, or default(TSource) when empty; throws when more than one.</summary>
    public static TSource? SingleOrDefault<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        IEnumerator<TSource> e = source.GetEnumerator();
        try
        {
            if (!e.MoveNext())
                return default;
            TSource result = e.Current;
            if (e.MoveNext())
                throw new InvalidOperationException("Sequence contains more than one element");
            return result;
        }
        finally { e.Dispose(); }
    }

    /// <summary>Returns the element at a zero-based index, or throws when out of range.</summary>
    public static TSource ElementAt<TSource>(this IEnumerable<TSource> source, int index)
    {
        ThrowIfNull(source, "source");
        if (index < 0)
            throw new ArgumentOutOfRangeException("index");
        int i = 0;
        foreach (TSource item in source)
        {
            if (i == index)
                return item;
            i++;
        }
        throw new ArgumentOutOfRangeException("index");
    }

    /// <summary>Returns the element at a zero-based index, or default(TSource) when out of range.</summary>
    public static TSource? ElementAtOrDefault<TSource>(this IEnumerable<TSource> source, int index)
    {
        ThrowIfNull(source, "source");
        if (index < 0)
            return default;
        int i = 0;
        foreach (TSource item in source)
        {
            if (i == index)
                return item;
            i++;
        }
        return default;
    }

    // ==================== Quantifiers ====================

    /// <summary>Determines whether the sequence contains any elements.</summary>
    public static bool Any<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        IEnumerator<TSource> e = source.GetEnumerator();
        try
        {
            return e.MoveNext();
        }
        finally { e.Dispose(); }
    }

    /// <summary>Determines whether any element satisfies the predicate.</summary>
    public static bool Any<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(predicate, "predicate");
        foreach (TSource item in source)
        {
            if (predicate(item))
                return true;
        }
        return false;
    }

    /// <summary>Determines whether all elements satisfy the predicate (true for an empty sequence).</summary>
    public static bool All<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(predicate, "predicate");
        foreach (TSource item in source)
        {
            if (!predicate(item))
                return false;
        }
        return true;
    }

    /// <summary>Determines whether the sequence contains the specified value.</summary>
    public static bool Contains<TSource>(this IEnumerable<TSource> source, TSource value)
        => Contains(source, value, null);

    /// <summary>Determines whether the sequence contains the value using the comparer.</summary>
    public static bool Contains<TSource>(this IEnumerable<TSource> source, TSource value, IEqualityComparer<TSource>? comparer)
    {
        ThrowIfNull(source, "source");
        IEqualityComparer<TSource> cmp = comparer ?? EqualityComparer<TSource>.Default;
        foreach (TSource item in source)
        {
            if (cmp.Equals(item, value))
                return true;
        }
        return false;
    }

    // ==================== Aggregation ====================

    /// <summary>Returns the number of elements in the sequence.</summary>
    public static int Count<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        int count = 0;
        foreach (TSource item in source)
            count++;
        return count;
    }

    /// <summary>Returns the number of elements that satisfy the predicate.</summary>
    public static int Count<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(predicate, "predicate");
        int count = 0;
        foreach (TSource item in source)
        {
            if (predicate(item))
                count++;
        }
        return count;
    }

    /// <summary>Sums a sequence of Int32 values (0 for an empty sequence).</summary>
    public static int Sum(this IEnumerable<int> source)
    {
        ThrowIfNull(source, "source");
        int sum = 0;
        foreach (int item in source)
            sum += item;
        return sum;
    }

    /// <summary>Projects and sums Int32 values.</summary>
    public static int Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, int> selector)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(selector, "selector");
        int sum = 0;
        foreach (TSource item in source)
            sum += selector(item);
        return sum;
    }

    /// <summary>Sums a sequence of Int64 values.</summary>
    public static long Sum(this IEnumerable<long> source)
    {
        ThrowIfNull(source, "source");
        long sum = 0;
        foreach (long item in source)
            sum += item;
        return sum;
    }

    /// <summary>Sums a sequence of Double values.</summary>
    public static double Sum(this IEnumerable<double> source)
    {
        ThrowIfNull(source, "source");
        double sum = 0;
        foreach (double item in source)
            sum += item;
        return sum;
    }

    /// <summary>Projects and sums Double values.</summary>
    public static double Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, double> selector)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(selector, "selector");
        double sum = 0;
        foreach (TSource item in source)
            sum += selector(item);
        return sum;
    }

    /// <summary>Returns the minimum Int32 value (throws for an empty sequence).</summary>
    public static int Min(this IEnumerable<int> source)
    {
        ThrowIfNull(source, "source");
        int min = 0;
        bool found = false;
        foreach (int item in source)
        {
            if (!found || item < min) min = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return min;
    }

    /// <summary>Returns the maximum Int32 value (throws for an empty sequence).</summary>
    public static int Max(this IEnumerable<int> source)
    {
        ThrowIfNull(source, "source");
        int max = 0;
        bool found = false;
        foreach (int item in source)
        {
            if (!found || item > max) max = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return max;
    }

    /// <summary>Returns the minimum Double value (throws for an empty sequence).</summary>
    public static double Min(this IEnumerable<double> source)
    {
        ThrowIfNull(source, "source");
        double min = 0;
        bool found = false;
        foreach (double item in source)
        {
            if (!found || item < min) min = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return min;
    }

    /// <summary>Returns the maximum Double value (throws for an empty sequence).</summary>
    public static double Max(this IEnumerable<double> source)
    {
        ThrowIfNull(source, "source");
        double max = 0;
        bool found = false;
        foreach (double item in source)
        {
            if (!found || item > max) max = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return max;
    }

    /// <summary>Returns the minimum value using the comparer (throws for an empty sequence).</summary>
    public static TSource Min<TSource>(this IEnumerable<TSource> source, IComparer<TSource>? comparer = null)
    {
        ThrowIfNull(source, "source");
        IComparer<TSource> cmp = comparer ?? Comparer<TSource>.Default;
        bool found = false;
        TSource best = default!;
        foreach (TSource item in source)
        {
            if (!found || cmp.Compare(item, best) < 0)
                best = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return best;
    }

    /// <summary>Returns the maximum value using the comparer (throws for an empty sequence).</summary>
    public static TSource Max<TSource>(this IEnumerable<TSource> source, IComparer<TSource>? comparer = null)
    {
        ThrowIfNull(source, "source");
        IComparer<TSource> cmp = comparer ?? Comparer<TSource>.Default;
        bool found = false;
        TSource best = default!;
        foreach (TSource item in source)
        {
            if (!found || cmp.Compare(item, best) > 0)
                best = item;
            found = true;
        }
        if (!found)
            throw new InvalidOperationException("Sequence contains no elements");
        return best;
    }

    /// <summary>Computes the average of Int32 values (throws for an empty sequence).</summary>
    public static double Average(this IEnumerable<int> source)
    {
        ThrowIfNull(source, "source");
        long sum = 0;
        long count = 0;
        foreach (int item in source)
        {
            sum += item;
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("Sequence contains no elements");
        return (double)sum / count;
    }

    /// <summary>Computes the average of Double values (throws for an empty sequence).</summary>
    public static double Average(this IEnumerable<double> source)
    {
        ThrowIfNull(source, "source");
        double sum = 0;
        long count = 0;
        foreach (double item in source)
        {
            sum += item;
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("Sequence contains no elements");
        return sum / count;
    }

    // ==================== Conversion ====================

    /// <summary>Creates a List&lt;T&gt; from the sequence (eager).</summary>
    public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        List<TSource> list = new List<TSource>();
        foreach (TSource item in source)
            list.Add(item);
        return list;
    }

    /// <summary>Creates an array from the sequence (eager).</summary>
    public static TSource[] ToArray<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        List<TSource> list = ToList(source);
        TSource[] array = new TSource[list.Count];
        for (int i = 0; i < list.Count; i++)
            array[i] = list[i];
        return array;
    }

    /// <summary>Creates a Dictionary from the sequence using the key selector (eager).</summary>
    public static Dictionary<TKey, TSource> ToDictionary<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        where TKey : notnull
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(keySelector, "keySelector");
        Dictionary<TKey, TSource> dictionary = new Dictionary<TKey, TSource>();
        foreach (TSource item in source)
            dictionary.Add(keySelector(item), item);
        return dictionary;
    }

    // ==================== Set operators ====================

    /// <summary>Returns distinct elements (deferred; hashes with the comparer).</summary>
    public static IEnumerable<TSource> Distinct<TSource>(this IEnumerable<TSource> source)
        => Distinct(source, null);

    /// <summary>Returns distinct elements using the comparer (deferred).</summary>
    public static IEnumerable<TSource> Distinct<TSource>(
        this IEnumerable<TSource> source, IEqualityComparer<TSource>? comparer)
    {
        ThrowIfNull(source, "source");
        return new DistinctEnumerable<TSource>(source, comparer);
    }

    private sealed class DistinctEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;
        private readonly IEqualityComparer<T>? _comparer;
        public DistinctEnumerable(IEnumerable<T> source, IEqualityComparer<T>? comparer)
        { _source = source; _comparer = comparer; }
        public IEnumerator<T> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _comparer);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly IEnumerator<T> _inner;
            private readonly HashSet<T> _seen;
            public Enumerator(IEnumerator<T> inner, IEqualityComparer<T>? comparer)
            { _inner = inner; _seen = new HashSet<T>(comparer); }
            public T Current => _inner.Current;
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                while (_inner.MoveNext())
                {
                    if (_seen.Add(_inner.Current))
                        return true;
                }
                return false;
            }
            public void Reset() { _inner.Reset(); _seen.Clear(); }
            public void Dispose() => _inner.Dispose();
        }
    }

    // ==================== Partitioning ====================

    /// <summary>Returns the first <paramref name="count"/> elements (deferred).</summary>
    public static IEnumerable<TSource> Take<TSource>(this IEnumerable<TSource> source, int count)
    {
        ThrowIfNull(source, "source");
        return new TakeEnumerable<TSource>(source, count);
    }

    private sealed class TakeEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;
        private readonly int _count;
        public TakeEnumerable(IEnumerable<T> source, int count) { _source = source; _count = count; }
        public IEnumerator<T> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _count);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly IEnumerator<T> _inner;
            private int _remaining;
            public Enumerator(IEnumerator<T> inner, int count) { _inner = inner; _remaining = count; }
            public T Current => _inner.Current;
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                if (_remaining <= 0) return false;
                if (!_inner.MoveNext())
                {
                    _remaining = 0;
                    return false;
                }
                _remaining--;
                return true;
            }
            public void Reset() => _inner.Reset();
            public void Dispose() => _inner.Dispose();
        }
    }

    /// <summary>Skips the first <paramref name="count"/> elements (deferred).</summary>
    public static IEnumerable<TSource> Skip<TSource>(this IEnumerable<TSource> source, int count)
    {
        ThrowIfNull(source, "source");
        return new SkipEnumerable<TSource>(source, count);
    }

    private sealed class SkipEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;
        private readonly int _count;
        public SkipEnumerable(IEnumerable<T> source, int count) { _source = source; _count = count; }
        public IEnumerator<T> GetEnumerator() => new Enumerator(_source.GetEnumerator(), _count);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly IEnumerator<T> _inner;
            private int _remaining;
            public Enumerator(IEnumerator<T> inner, int count) { _inner = inner; _remaining = count; }
            public T Current => _inner.Current;
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                while (_remaining > 0 && _inner.MoveNext())
                    _remaining--;
                return _remaining <= 0 && _inner.MoveNext();
            }
            public void Reset() => _inner.Reset();
            public void Dispose() => _inner.Dispose();
        }
    }

    // ==================== Concatenation ====================

    /// <summary>Concatenates two sequences (deferred).</summary>
    public static IEnumerable<TSource> Concat<TSource>(this IEnumerable<TSource> first, IEnumerable<TSource> second)
    {
        ThrowIfNull(first, "first");
        ThrowIfNull(second, "second");
        return new ConcatEnumerable<TSource>(first, second);
    }

    private sealed class ConcatEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _first;
        private readonly IEnumerable<T> _second;
        public ConcatEnumerable(IEnumerable<T> first, IEnumerable<T> second) { _first = first; _second = second; }
        public IEnumerator<T> GetEnumerator() => new Enumerator(_first.GetEnumerator(), _second.GetEnumerator());
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<T>
        {
            private IEnumerator<T> _current;
            private readonly IEnumerator<T> _second;
            private bool _inSecond;
            public Enumerator(IEnumerator<T> first, IEnumerator<T> second) { _current = first; _second = second; }
            public T Current => _current.Current;
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                if (!_inSecond)
                {
                    if (_current.MoveNext())
                        return true;
                    _current.Dispose();
                    _current = _second;
                    _inSecond = true;
                }
                return _current.MoveNext();
            }
            public void Reset() { _current.Reset(); _inSecond = false; }
            public void Dispose() => _current.Dispose();
        }
    }

    /// <summary>Inverts the order of the elements (deferred; buffers on first enumeration).</summary>
    public static IEnumerable<TSource> Reverse<TSource>(this IEnumerable<TSource> source)
    {
        ThrowIfNull(source, "source");
        return new ReverseEnumerable<TSource>(source);
    }

    private sealed class ReverseEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;
        public ReverseEnumerable(IEnumerable<T> source) { _source = source; }
        public IEnumerator<T> GetEnumerator()
        {
            List<T> buffer = new List<T>();
            foreach (T item in _source)
                buffer.Add(item);
            return new Enumerator(buffer);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly List<T> _buffer;
            private int _index;
            public Enumerator(List<T> buffer) { _buffer = buffer; _index = buffer.Count; }
            public T Current => _buffer[_index];
            object IEnumerator.Current => Current!;
            public bool MoveNext()
            {
                if (_index <= 0) return false;
                _index--;
                return true;
            }
            public void Reset() => _index = _buffer.Count;
            public void Dispose() { }
        }
    }

    // ==================== Ordering ====================

    /// <summary>
    /// Sorts the elements in ascending order of the key (deferred; the
    /// source is buffered on first enumeration). The sort is stable.
    /// </summary>
    public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        => OrderBy(source, keySelector, null);

    /// <summary>OrderBy with an explicit key comparer.</summary>
    public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(keySelector, "keySelector");
        return new OrderedEnumerable<TSource>(source, x => keySelector(x), new BoxedKeyComparer<TKey>(comparer, false), false);
    }

    /// <summary>Sorts the elements in descending order of the key (deferred; stable).</summary>
    public static IOrderedEnumerable<TSource> OrderByDescending<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        => OrderByDescending(source, keySelector, null);

    /// <summary>OrderByDescending with an explicit key comparer.</summary>
    public static IOrderedEnumerable<TSource> OrderByDescending<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(keySelector, "keySelector");
        return new OrderedEnumerable<TSource>(source, x => keySelector(x), new BoxedKeyComparer<TKey>(comparer, true), true);
    }

    /// <summary>Adds an ascending secondary key to an ordered sequence.</summary>
    public static IOrderedEnumerable<TSource> ThenBy<TSource, TKey>(
        this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        => ThenBy(source, keySelector, null);

    /// <summary>ThenBy with an explicit key comparer.</summary>
    public static IOrderedEnumerable<TSource> ThenBy<TSource, TKey>(
        this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(keySelector, "keySelector");
        return source.CreateOrderedEnumerable(keySelector, comparer, false);
    }

    /// <summary>Adds a descending secondary key to an ordered sequence.</summary>
    public static IOrderedEnumerable<TSource> ThenByDescending<TSource, TKey>(
        this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        => ThenByDescending(source, keySelector, null);

    /// <summary>ThenByDescending with an explicit key comparer.</summary>
    public static IOrderedEnumerable<TSource> ThenByDescending<TSource, TKey>(
        this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(keySelector, "keySelector");
        return source.CreateOrderedEnumerable(keySelector, comparer, true);
    }

    /// <summary>
    /// The ordered sequence implementation. Each OrderBy(...) creates one
    /// with a single key; ThenBy(...) clones the chain with an additional
    /// key. Enumeration buffers the source, applies the composed key
    /// comparison with a stable insertion sort, and yields the elements.
    /// </summary>
    private sealed class OrderedEnumerable<T> : IOrderedEnumerable<T>
    {
        private readonly IEnumerable<T>? _source;          // set on the root (OrderBy)
        private readonly OrderedEnumerable<T>? _parent;    // set on ThenBy levels
        private readonly Func<T, object?> _keySelector;    // boxed key (uniform storage across key types)
        private readonly IComparer<object?> _comparer;     // boxes/normalizes key comparison
        private readonly bool _descending;

        public OrderedEnumerable(IEnumerable<T> source, Func<T, object?> keySelector, IComparer<object?> comparer, bool descending)
        {
            _source = source;
            _keySelector = keySelector;
            _comparer = comparer;
            _descending = descending;
        }

        private OrderedEnumerable(OrderedEnumerable<T> parent, Func<T, object?> keySelector, IComparer<object?> comparer, bool descending)
        {
            _parent = parent;
            _keySelector = keySelector;
            _comparer = comparer;
            _descending = descending;
        }

        public IOrderedEnumerable<T> CreateOrderedEnumerable<TKey>(
            Func<T, TKey> keySelector, IComparer<TKey>? comparer, bool descending)
        {
            return new OrderedEnumerable<T>(this, x => keySelector(x), new BoxedKeyComparer<TKey>(comparer, descending), descending);
        }

        public IEnumerator<T> GetEnumerator()
        {
            // Collect the key levels from the outermost (this) to the root.
            List<OrderedEnumerable<T>> levels = new List<OrderedEnumerable<T>>();
            OrderedEnumerable<T>? level = this;
            while (level != null)
            {
                levels.Add(level);
                level = level._parent;
            }
            OrderedEnumerable<T> root = levels[levels.Count - 1];

            List<T> buffer = new List<T>();
            foreach (T item in root._source!)
                buffer.Add(item);

            StableSort(buffer, levels);

            return buffer.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Stable insertion sort by the composed key chain.</summary>
        private static void StableSort(List<T> buffer, List<OrderedEnumerable<T>> levels)
        {
            for (int i = 1; i < buffer.Count; i++)
            {
                T item = buffer[i];
                int j = i - 1;
                while (j >= 0 && CompareKeyChain(levels, buffer[j], item) > 0)
                {
                    buffer[j + 1] = buffer[j];
                    j--;
                }
                buffer[j + 1] = item;
            }
        }

        private static int CompareKeyChain(List<OrderedEnumerable<T>> levels, T x, T y)
        {
            // levels is ordered outermost-first (the most recent ThenBy at [0],
            // the original OrderBy last). The OrderBy key is the primary key, so
            // walk from the root back towards the outermost level.
            for (int i = levels.Count - 1; i >= 0; i--)
            {
                OrderedEnumerable<T> lvl = levels[i];
                int c = lvl._comparer.Compare(lvl._keySelector(x), lvl._keySelector(y));
                if (c != 0)
                    return c;
            }
            return 0;
        }
    }

    /// <summary>Compares boxed keys of <typeparamref name="TKey"/> with the requested direction.</summary>
    private sealed class BoxedKeyComparer<TKey> : IComparer<object?>
    {
        private readonly IComparer<TKey> _inner;
        private readonly bool _descending;
        public BoxedKeyComparer(IComparer<TKey>? comparer, bool descending)
        {
            _inner = comparer ?? Comparer<TKey>.Default;
            _descending = descending;
        }
        public int Compare(object? x, object? y)
        {
            int c = _inner.Compare((TKey)x!, (TKey)y!);
            return _descending ? -c : c;
        }
    }

    // ==================== Grouping ====================

    /// <summary>
    /// Groups the elements by key (deferred; the source is buffered into a
    /// dictionary on first enumeration). Groups enumerate in first-seen
    /// key order, matching the official implementation.
    /// </summary>
    public static IEnumerable<IGrouping<TKey, TSource>> GroupBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        where TKey : notnull
        => GroupBy(source, keySelector, null);

    /// <summary>GroupBy with an explicit key comparer.</summary>
    public static IEnumerable<IGrouping<TKey, TSource>> GroupBy<TSource, TKey>(
        this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IEqualityComparer<TKey>? comparer)
        where TKey : notnull
    {
        ThrowIfNull(source, "source");
        ThrowIfNull(keySelector, "keySelector");
        return new GroupedEnumerable<TSource, TKey>(source, keySelector, comparer);
    }

    private sealed class GroupedEnumerable<TSource, TKey> : IEnumerable<IGrouping<TKey, TSource>>
        where TKey : notnull
    {
        private readonly IEnumerable<TSource> _source;
        private readonly Func<TSource, TKey> _keySelector;
        private readonly IEqualityComparer<TKey>? _comparer;
        public GroupedEnumerable(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IEqualityComparer<TKey>? comparer)
        { _source = source; _keySelector = keySelector; _comparer = comparer; }

        public IEnumerator<IGrouping<TKey, TSource>> GetEnumerator()
        {
            IEqualityComparer<TKey> cmp = _comparer ?? EqualityComparer<TKey>.Default;
            Dictionary<TKey, Grouping> byKey = new Dictionary<TKey, Grouping>(cmp);
            List<Grouping> ordered = new List<Grouping>();
            foreach (TSource item in _source)
            {
                TKey key = _keySelector(item);
                Grouping group;
                if (!byKey.TryGetValue(key, out group!))
                {
                    group = new Grouping(key);
                    byKey.Add(key, group);
                    ordered.Add(group);
                }
                group.Add(item);
            }
            List<IGrouping<TKey, TSource>> groups = new List<IGrouping<TKey, TSource>>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
                groups.Add(ordered[i]);
            return groups.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Grouping : IGrouping<TKey, TSource>
        {
            private readonly TKey _key;
            private readonly List<TSource> _items = new List<TSource>();
            public Grouping(TKey key) { _key = key; }
            public TKey Key => _key;
            public void Add(TSource item) => _items.Add(item);
            public IEnumerator<TSource> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    // ==================== Join ====================

    /// <summary>
    /// Correlates the elements of two sequences based on matching keys
    /// (inner join; deferred; the inner sequence is buffered into a lookup
    /// on first enumeration). Matches within a key produce every
    /// outer x inner combination.
    /// </summary>
    public static IEnumerable<TResult> Join<TOuter, TInner, TKey, TResult>(
        this IEnumerable<TOuter> outer,
        IEnumerable<TInner> inner,
        Func<TOuter, TKey> outerKeySelector,
        Func<TInner, TKey> innerKeySelector,
        Func<TOuter, TInner, TResult> resultSelector)
        where TKey : notnull
        => Join(outer, inner, outerKeySelector, innerKeySelector, resultSelector, null);

    /// <summary>Join with an explicit key comparer.</summary>
    public static IEnumerable<TResult> Join<TOuter, TInner, TKey, TResult>(
        this IEnumerable<TOuter> outer,
        IEnumerable<TInner> inner,
        Func<TOuter, TKey> outerKeySelector,
        Func<TInner, TKey> innerKeySelector,
        Func<TOuter, TInner, TResult> resultSelector,
        IEqualityComparer<TKey>? comparer)
        where TKey : notnull
    {
        ThrowIfNull(outer, "outer");
        ThrowIfNull(inner, "inner");
        ThrowIfNull(outerKeySelector, "outerKeySelector");
        ThrowIfNull(innerKeySelector, "innerKeySelector");
        ThrowIfNull(resultSelector, "resultSelector");
        return new JoinEnumerable<TOuter, TInner, TKey, TResult>(
            outer, inner, outerKeySelector, innerKeySelector, resultSelector, comparer);
    }

    private sealed class JoinEnumerable<TOuter, TInner, TKey, TResult> : IEnumerable<TResult>
        where TKey : notnull
    {
        private readonly IEnumerable<TOuter> _outer;
        private readonly IEnumerable<TInner> _inner;
        private readonly Func<TOuter, TKey> _outerKeySelector;
        private readonly Func<TInner, TKey> _innerKeySelector;
        private readonly Func<TOuter, TInner, TResult> _resultSelector;
        private readonly IEqualityComparer<TKey>? _comparer;

        public JoinEnumerable(
            IEnumerable<TOuter> outer, IEnumerable<TInner> inner,
            Func<TOuter, TKey> outerKeySelector, Func<TInner, TKey> innerKeySelector,
            Func<TOuter, TInner, TResult> resultSelector, IEqualityComparer<TKey>? comparer)
        {
            _outer = outer; _inner = inner;
            _outerKeySelector = outerKeySelector; _innerKeySelector = innerKeySelector;
            _resultSelector = resultSelector; _comparer = comparer;
        }

        public IEnumerator<TResult> GetEnumerator()
        {
            IEqualityComparer<TKey> cmp = _comparer ?? EqualityComparer<TKey>.Default;
            Dictionary<TKey, List<TInner>> lookup = new Dictionary<TKey, List<TInner>>(cmp);
            foreach (TInner item in _inner)
            {
                TKey key = _innerKeySelector(item);
                List<TInner> bucket;
                if (!lookup.TryGetValue(key, out bucket!))
                {
                    bucket = new List<TInner>();
                    lookup.Add(key, bucket);
                }
                bucket.Add(item);
            }

            List<TResult> results = new List<TResult>();
            foreach (TOuter outerItem in _outer)
            {
                List<TInner> bucket;
                if (lookup.TryGetValue(_outerKeySelector(outerItem), out bucket!))
                {
                    for (int i = 0; i < bucket.Count; i++)
                        results.Add(_resultSelector(outerItem, bucket[i]));
                }
            }
            return results.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
