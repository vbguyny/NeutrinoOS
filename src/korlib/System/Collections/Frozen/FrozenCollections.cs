// NeutrinoOS korlib - System.Collections.Frozen
//
// Humanizer 3.0.10 materialises its localiser registries with
// `localisersBuilder.ToFrozenDictionary(null)` and its enum metadata with
// `ToFrozenSet()`. The device has no optimised frozen-collection
// implementations (no alternate lookup, no specialised hashing), so these are
// thin immutable views over Dictionary<TKey,TValue> / HashSet<T>: correct and
// predictable, just not cache-friendly.

using System.Collections.Generic;

namespace System.Collections.Frozen
{
    public static class FrozenDictionary
    {
        public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(
            IEnumerable<KeyValuePair<TKey, TValue>> source)
            => ToFrozenDictionary(source, null);

        public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(
            IEnumerable<KeyValuePair<TKey, TValue>> source, IEqualityComparer<TKey>? comparer)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var d = new Dictionary<TKey, TValue>(comparer);
            foreach (var kv in source) d[kv.Key] = kv.Value;
            return new FrozenDictionary<TKey, TValue>(d);
        }

        public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TSource, TKey, TValue>(
            IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> elementSelector)
            => ToFrozenDictionary(source, keySelector, elementSelector, null);

        public static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TSource, TKey, TValue>(
            IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> elementSelector,
            IEqualityComparer<TKey>? comparer)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));
            if (elementSelector == null) throw new ArgumentNullException(nameof(elementSelector));
            var d = new Dictionary<TKey, TValue>(comparer);
            foreach (var item in source) d[keySelector(item)] = elementSelector(item);
            return new FrozenDictionary<TKey, TValue>(d);
        }
    }

    public sealed class FrozenDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    {
        private readonly Dictionary<TKey, TValue> _inner;

        internal FrozenDictionary(Dictionary<TKey, TValue> inner) { _inner = inner; }

        public int Count => _inner.Count;

        public TValue this[TKey key] => _inner[key];

        public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value!);

        public bool ContainsKey(TKey key) => _inner.ContainsKey(key);

        public IEnumerable<TKey> Keys => _inner.Keys;

        public IEnumerable<TValue> Values => _inner.Values;

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }

    public static class FrozenSet
    {
        public static FrozenSet<T> ToFrozenSet<T>(IEnumerable<T> source) => ToFrozenSet(source, null);

        public static FrozenSet<T> ToFrozenSet<T>(IEnumerable<T> source, IEqualityComparer<T>? comparer)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var s = new HashSet<T>(comparer);
            foreach (var item in source) s.Add(item);
            return new FrozenSet<T>(s);
        }
    }

    public sealed class FrozenSet<T> : IEnumerable<T>
    {
        private readonly HashSet<T> _inner;

        internal FrozenSet(HashSet<T> inner) { _inner = inner; }

        public int Count => _inner.Count;

        public bool Contains(T item) => _inner.Contains(item);

        public bool TryGetValue(T equalValue, out T actualValue)
        {
            if (_inner.Contains(equalValue)) { actualValue = equalValue; return true; }
            actualValue = default(T)!;
            return false;
        }

        public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();
    }
}
